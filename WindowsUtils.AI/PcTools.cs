using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluentResults;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using WindowsUtils.Core;
using WindowsUtils.Core.Hashing;
using WindowsUtils.Core.IO;
using WindowsUtils.Core.Net;
using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.AI;

/// <summary>
/// Read-only PC inspection tools exposed to the AI agent. All methods are plain
/// static functions, so they are also directly usable from console apps and tests.
/// Each tool is a railway: checks (<see cref="RequireDirectory"/>, <see cref="RequireLocalFile"/>, ...)
/// and Core calls are chained with Bind/Map over <see cref="Result{TValue}"/>, and <see cref="Run"/>
/// turns the outcome, or any unexpected exception, into the text the model sees.
/// </summary>
public static class PcTools
{
    // Concrete OpenAI client (not IChatClient): the 7-argument AsAIAgent overload
    // used by ChatSession is defined on this type.
    internal static ChatClient CreateChatClient(string model, string apiKey, Uri endpoint) =>
        new(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });

    // One tool for the AI agent: the function itself, plus whether it reads private data
    // (file contents or environment variable values) and must ask the user before each run,
    // and whether it talks to the outside world (network/LLM) rather than this PC only.
    internal readonly record struct PcTool(AIFunction Function, bool NeedsApproval, bool OpenWorld = false);

    // AIFunctionFactory marshals results to JsonElement by default. Over MCP the SDK would then
    // serialize that JsonElement a second time, so text arrived quoted with visible \n escapes
    // (McpServerHost.cs). Pass the raw result through instead; every consumer gets the plain string.
    // Use this factory for every tool exposed to the agent or over MCP.
    internal static AIFunction CreateFunction(Delegate method, string? name = null, string? description = null) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
        });

    /// <summary>The read-only PC inspection tools. Single source of truth: the chat agent
    /// (<see cref="ChatSession"/>) and the MCP server (<see cref="McpServerHost"/>) both use this list.</summary>
    internal static PcTool[] CreateTools() =>
    [
        new(CreateFunction(new Func<string>(GetCurrentTime)), false),
        new(CreateFunction(new Func<string>(GetSystemInfo)), false),
        new(CreateFunction(new Func<string>(GetNetFrameworkVersion)), false),
        new(CreateFunction(new Func<string>(GetJavaVersion)), false),
        new(CreateFunction(new Func<string>(GetPythonVersion)), false),
        new(CreateFunction(new Func<string>(GetNodeVersion)), false),
        new(CreateFunction(new Func<string>(GetWindowsFeatures)), false),
        new(CreateFunction(new Func<int, string>(GetProcesses)), false),
        new(CreateFunction(new Func<string>(GetDrives)), false),
        new(CreateFunction(new Func<string, string, int, string>(ListFiles)), false),
        new(CreateFunction(new Func<string, string, int, string>(ListFolders)), false),
        new(CreateFunction(new Func<string, int, bool, string>(GetLargestFiles)), false),
        new(CreateFunction(new Func<string, int, int, CancellationToken, string>(FindDuplicateFiles)), false),
        new(CreateFunction(new Func<string, int, CancellationToken, string>(GetFolderSizes)), false),
        new(CreateFunction(new Func<CancellationToken, string>(GetTempCleanupSizes)), false),
        new(CreateFunction(new Func<string>(GetStartupPrograms)), false),
        new(CreateFunction(new Func<string>(GetBatteryReport)), false),
        new(CreateFunction(new Func<string, string, int, string>(GetInstalledPrograms)), false),
        new(CreateFunction(new Func<string>(GetLocalUsers)), false),
        new(CreateFunction(new Func<string>(GetNetworkAdapters)), false),
        new(CreateFunction(new Func<string>(GetFirewallPorts)), false),
        // Outbound network calls deserve the same gate as private-data reads: the host name is
        // resolved via DNS, so embedded data (e.g. exfiltrated from an approved file) reaches the
        // attacker's own server, and traceroute probes inner networks. Reads of file contents and
        // environment values (which can hold secrets) come with the same rule. Chat wraps these in
        // ApprovalRequiredAIFunction; the MCP server in its approval gate.
        new(CreateFunction(new Func<string, int, CancellationToken, Task<string>>(PingHost)), true, true),
        new(CreateFunction(new Func<string, int, int, bool, int, CancellationToken, Task<string>>(TraceTcpRoute)), true, true),
        new(CreateFunction(new Func<string, int, string>(ReadTextFile)), true),
        new(CreateFunction(new Func<string, string>(GetEnvironmentVariable)), true),
        new(CreateFunction(new Func<string, int, string>(ListEnvironmentVariables)), true),
        new(CreateFunction(new Func<string, string, string>(ComputeFileHash)), false),
        new(CreateFunction(new Func<string, string, string>(VerifyFileHash)), false),
    ];

    [Description("Gets the current date and time on this PC.")]
    public static string GetCurrentTime() => Run(() => DateTime.Now.ToString("F"));

    [Description("Gets system info: machine, user, OS and architecture, CPU name, logical processors, "
        + "total/available RAM and RAM in use, uptime, system directory, screen resolution and the .NET runtime.")]
    public static string GetSystemInfo() => Run(() =>
    {
        var lines = new List<string>
        {
            $"Machine: {Environment.MachineName}",
            $"User: {Environment.UserName}",
            $"OS: {Environment.OSVersion} (64-bit OS: {Environment.Is64BitOperatingSystem})",
            $"OS Architecture: {RuntimeInformation.OSArchitecture}",
            $"CPU: {SystemDetails.CpuName()}",
            $"Processors: {Environment.ProcessorCount}",
        };

        var (total, available) = SystemDetails.Memory();
        if (total > 0)
        {
            lines.Add($"Total RAM: {ByteFormatter.FormatBytes((long)total)}");
            lines.Add($"Available RAM: {ByteFormatter.FormatBytes((long)available)}");
            lines.Add($"RAM In Use: {100 - available * 100.0 / total:0.#}%");
        }

        var uptime = SystemDetails.Uptime();
        lines.Add($"System Uptime: {(int)uptime.TotalDays} days, {uptime.Hours}h {uptime.Minutes}m");

        var systemDirectory = SystemDetails.GetSystemDirectory();
        if (systemDirectory.Length > 0)
            lines.Add($"System Directory: {systemDirectory}");

        var (width, height) = SystemDetails.ScreenSize();
        if (width > 0 && height > 0)
            lines.Add($"Screen Resolution: {width} x {height}");

        lines.Add($"Runtime: {Environment.Version}");
        lines.Add($"Framework: {RuntimeInformation.FrameworkDescription}");
        return Lines(lines);
    });

    [Description("Gets the installed .NET Framework version(s) (4.5-4.8.1 from the v4 Release value, plus older "
        + "side-by-side 3.5/3.0/2.0/1.1 installs): version, build number, folder and registry key. "
        + "The first one listed is the newest. This is .NET Framework, not modern .NET (5+). Read-only.")]
    public static string GetNetFrameworkVersion() =>
        DescribeRuntimes(NetFrameworkDetector.Find, ".NET Framework", @"the NET Framework Setup\NDP registry keys");

    [Description("Gets the installed Java version(s) (JDK/JRE): version, vendor, install folder and where it was found "
        + "(JAVA_HOME, registry or PATH). The first one listed is the default. Read-only.")]
    public static string GetJavaVersion() =>
        DescribeRuntimes(JavaDetector.Find, "Java", "JAVA_HOME, the registry or PATH");

    [Description("Gets the installed Python version(s): version, vendor, install folder and where it was found "
        + "(PATH, PEP 514 registry keys as listed by the py launcher, the Python install manager, or pyenv). "
        + "The first one listed is the default. Read-only.")]
    public static string GetPythonVersion() =>
        DescribeRuntimes(PythonDetector.Find, "Python", "PATH, the registry, the Python install manager or pyenv");

    [Description("Gets the installed Node.js version(s): version, install folder and where it was found "
        + "(PATH, installer registry key, nvm, Volta or fnm). The first one listed is the default. Read-only.")]
    public static string GetNodeVersion() =>
        DescribeRuntimes(NodeDetector.Find, "Node.js", "PATH, the registry, nvm, Volta or fnm");

    private static string DescribeRuntimes(Func<IReadOnlyList<RuntimeInstallation>> find, string name, string searched) =>
        Run(() =>
        {
            var installs = find();
            if (installs.Count == 0)
                return $"{name} is not installed (nothing found via {searched}).";
            return Lines(installs.Take(20).Select((r, i) =>
                $"{(i == 0 ? "Default: " : "")}{r.Display} at {r.Home} [{r.Source}]"));
        });

    [Description("Checks which optional Windows features are enabled: Telnet Client/Server, TFTP, IIS (and its W3SVC service), "
        + "IIS FTP, SMB 1.0, Hyper-V, WSL, Virtual Machine Platform, Windows Sandbox, .NET Framework 3.5, OpenSSH Server. Read-only.")]
    public static string GetWindowsFeatures() => Run(() =>
        Result.Ok(WindowsFeatures.Get())
            .Bind(features => features.Count == 0
                ? Result.Fail<string>("(could not read Windows features)")
                : Lines(features.Select(f => $"{f.Name} [{f.Id}]: {f.Display}"))));

    [Description("Lists the top running processes by memory usage.")]
    public static string GetProcesses(
        [Description("Maximum number of processes to return (1-30).")] int maxProcesses = 10) => Run(() =>
    {
        var rows = new List<(string Name, int Id, long Mb)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                long mb;
                try
                {
                    mb = process.WorkingSet64 / 1024 / 1024;
                }
                catch
                {
                    continue;
                }
                rows.Add((process.ProcessName, process.Id, mb));
            }
        }
        return Lines(rows.OrderByDescending(r => r.Mb).Take(Math.Clamp(maxProcesses, 1, 30))
            .Select(r => $"{r.Name} (Id {r.Id}, {r.Mb} MB)"));
    });

    [Description("Lists drives with type, volume label, file system and free/total space (and free %).")]
    public static string GetDrives() => Run(() => Lines(DriveInfo.GetDrives().Select(d =>
    {
        if (!d.IsReady)
            return $"{d.Name} ({d.DriveType}, not ready)";
        try
        {
            var freePercent = d.TotalSize > 0 ? d.AvailableFreeSpace * 100.0 / d.TotalSize : 0;
            return $"{d.Name} ({d.DriveType}, {d.VolumeLabel}, {d.DriveFormat}) "
                + $"free {ByteFormatter.FormatBytes(d.AvailableFreeSpace)} of {ByteFormatter.FormatBytes(d.TotalSize)} "
                + $"({freePercent:0.#}% free)";
        }
        catch (Exception)
        {
            return $"{d.Name} ({d.DriveType}, not readable)";
        }
    })));

    [Description("Lists files and folders in a directory. Read-only.")]
    public static string ListFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory,
        [Description("Search pattern, e.g. *.log.")] string pattern = "*.*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50) => Run(() =>
        RequireDirectory(directory)
            .Map(resolved => Directory.EnumerateFileSystemEntries(resolved, OrDefault(pattern, "*.*"))
                .Take(Math.Clamp(maxResults, 1, 100)).ToArray())
            .Map(entries => entries.Length == 0 ? "(empty)" : Lines(entries)));

    [Description("Lists subfolders in a directory. Read-only.")]
    public static string ListFolders(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory,
        [Description("Search pattern, e.g. Proj*.")] string pattern = "*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50) => Run(() =>
        RequireDirectory(directory)
            .Map(resolved => Directory.EnumerateDirectories(resolved, OrDefault(pattern, "*"))
                .Take(Math.Clamp(maxResults, 1, 100)).ToArray())
            .Map(entries => entries.Length == 0 ? "(empty)" : Lines(entries)));

    [Description("Lists the largest files in a folder, sorted by size descending. Use for 'biggest files' questions.")]
    public static string GetLargestFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory = "Documents",
        [Description("How many files to return (1-50).")] int top = 10,
        [Description("Search subfolders too.")] bool recursive = true) => Run(() =>
        RequireDirectory(directory)
            .Bind(resolved => WithoutScanErrors(FileScanner.FindLargestFiles([resolved], Math.Clamp(top, 1, 50), recursive: recursive)))
            .Map(scan => scan.Items.Count == 0
                ? "(empty)"
                : Lines(scan.Items.Select(f => $"{ByteFormatter.FormatBytes(f.Size),-10} {f.FullPath}"))));

    [Description("Finds duplicate files (identical content, confirmed by SHA-256) in a folder and its subfolders. "
        + "Returns groups sorted by space that could be freed. Read-only: it never deletes anything.")]
    public static string FindDuplicateFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads, UserProfile.")] string directory = "Documents",
        [Description("Ignore files smaller than this many megabytes (0 = any size).")] int minSizeMb = 1,
        [Description("Maximum number of duplicate groups to return (1-50).")] int maxGroups = 20,
        CancellationToken cancellationToken = default) => Run(() =>
        RequireDirectory(directory)
            .Bind(resolved => WithoutScanErrors(DuplicateFinder.FindDuplicates(
                [resolved], Math.Max(0, minSizeMb) * 1024L * 1024L, cancellationToken: cancellationToken)))
            .Map(scan => DescribeDuplicates(scan.Items, scan.Scanned, Math.Clamp(maxGroups, 1, 50))));

    private static string DescribeDuplicates(IReadOnlyList<DuplicateGroup> groups, long scanned, int maxGroups)
    {
        if (groups.Count == 0)
            return $"No duplicates found ({scanned:N0} files scanned).";

        var lines = new List<string>
        {
            $"{groups.Count:N0} duplicate groups in {scanned:N0} files; "
                + $"{ByteFormatter.FormatBytes(groups.Sum(g => g.WastedBytes))} could be freed by keeping one copy of each."
                + (groups.Count > maxGroups ? $" Showing the top {maxGroups}." : ""),
        };
        foreach (var group in groups.Take(maxGroups))
        {
            lines.Add($"- {group.Files.Count} copies of {ByteFormatter.FormatBytes(group.Size)} "
                + $"(wastes {ByteFormatter.FormatBytes(group.WastedBytes)}), oldest first:");
            lines.AddRange(group.Files.Take(10).Select(f => $"    {f.FullPath}  ({f.Modified:g})"));
            if (group.Files.Count > 10)
                lines.Add($"    ...and {group.Files.Count - 10} more");
        }
        return Lines(lines);
    }

    [Description("Shows what takes up space inside a folder: its direct subfolders (with total size) and files, largest first. Read-only.")]
    public static string GetFolderSizes(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads, UserProfile.")] string directory = "Documents",
        [Description("Maximum items to return (1-50).")] int top = 20,
        CancellationToken cancellationToken = default) => Run(() =>
        RequireDirectory(directory)
            .Map(resolved => (Folder: resolved, Items: FileScanner.GetFolderContents(resolved, cancellationToken)))
            .Map(scan =>
            {
                if (scan.Items.Count == 0)
                    return "(empty)";
                var shown = Math.Clamp(top, 1, 50);
                var lines = new List<string>
                {
                    $"{scan.Folder}: {scan.Items.Count} items, total {ByteFormatter.FormatBytes(scan.Items.Sum(i => i.Size))}"
                        + (scan.Items.Count > shown ? $". Showing the largest {shown}." : "."),
                };
                lines.AddRange(scan.Items.Take(shown).Select(i =>
                    $"{ByteFormatter.FormatBytes(i.Size),-10} {(i.IsFolder ? "[folder]" : "[file]  ")} {i.Name}"));
                return Lines(lines);
            }));

    [Description("Reports how much space temporary and cache files take in the safe cleanup locations (user and Windows Temp, "
        + "thumbnail cache, Edge/Chrome/Firefox caches, error reports, crash dumps), and how much could be cleaned "
        + "(files older than 24 hours). Read-only: it never deletes anything; cleaning is done by the user on the Temp Cleaner screen.")]
    public static string GetTempCleanupSizes(CancellationToken cancellationToken = default) => Run(() =>
    {
        var sizes = TempCleaner.GetLocations().Select(l => TempCleaner.Measure(l, cancellationToken)).ToList();
        if (sizes.Count == 0)
            return "(no temp or cache locations found)";
        var lines = new List<string>
        {
            $"Total {ByteFormatter.FormatBytes(sizes.Sum(s => s.Bytes))}; "
                + $"{ByteFormatter.FormatBytes(sizes.Sum(s => s.CleanableBytes))} could be cleaned (older than 24 hours).",
        };
        lines.AddRange(sizes.Select(s =>
            $"{s.Location.Name}: {ByteFormatter.FormatBytes(s.Bytes)} in {s.Files:N0} files, "
            + $"cleanable {ByteFormatter.FormatBytes(s.CleanableBytes)} ({string.Join("; ", s.Location.Roots.Take(3))}"
            + (s.Location.Roots.Count > 3 ? $"; +{s.Location.Roots.Count - 3} more" : "") + ")"));
        return Lines(lines);
    });

    [Description("Battery and power report: power source (AC or battery), charge %, charging state, time remaining, "
        + "battery saver, the active power plan, and per battery its design and full-charge capacity, health % (wear), "
        + "cycle count and chemistry. Read-only.")]
    public static string GetBatteryReport() => Run(() =>
    {
        var report = PowerInfo.Get();
        var lines = new List<string>
        {
            $"Power source: {report.PowerSource}",
            $"Power plan: {report.PowerPlan ?? "unknown"}",
            $"Battery saver: {(report.BatterySaverOn ? "on" : "off")}",
        };
        if (!report.HasBattery)
        {
            lines.Add("Battery: none detected (desktop, server or virtual machine).");
            return Lines(lines);
        }
        lines.Add($"Charge: {(report.ChargePercent is int p ? $"{p}%" : "unknown")}, {report.ChargeState}");
        if (report.TimeRemaining is TimeSpan t)
            lines.Add($"Time remaining: {(int)t.TotalHours}h {t.Minutes}m");
        foreach (var b in report.Batteries)
        {
            lines.Add($"{b.Name}{(b.Manufacturer is null ? "" : $" ({b.Manufacturer})")}{(b.Chemistry is null ? "" : $", {b.Chemistry}")}: "
                + $"design {b.DesignCapacity?.ToString("N0") ?? "?"} mWh, full charge {b.FullChargeCapacity?.ToString("N0") ?? "?"} mWh, "
                + $"health {(b.HealthPercent is double h ? $"{h:0.#}%" : "unknown")}, cycles {b.CycleCount?.ToString() ?? "unknown"}"
                + (b.RateMilliwatts is long r ? $", {b.State?.ToLowerInvariant()} at {r / 1000.0:0.0} W" : ""));
        }
        return Lines(lines);
    });

    [Description("Lists installed programs (as in Apps / Programs and Features, from the registry Uninstall keys): "
        + "name, version, publisher, size and install date. Sort by name, size (largest first) or date (newest first). Read-only.")]
    public static string GetInstalledPrograms(
        [Description("Sort order: name, size or date.")] string sortBy = "name",
        [Description("Only programs whose name or publisher contains this text (empty = all).")] string filter = "",
        [Description("Maximum programs to return (1-200).")] int maxResults = 50) => Run(() =>
    {
        IEnumerable<InstalledProgram> programs = InstalledPrograms.Get();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var text = filter.Trim();
            programs = programs.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || p.Publisher.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        programs = sortBy.Trim().ToLowerInvariant() switch
        {
            "size" => programs.OrderByDescending(p => p.SizeBytes ?? -1),
            "date" or "installed" or "installdate" => programs.OrderByDescending(p => p.InstallDate ?? DateTime.MinValue),
            _ => programs,
        };
        var list = programs.ToList();
        if (list.Count == 0)
            return string.IsNullOrWhiteSpace(filter) ? "(no installed programs found)" : $"(no installed programs match '{filter}')";

        maxResults = Math.Clamp(maxResults, 1, 200);
        var lines = list.Take(maxResults).Select(p =>
            $"{p.Name}{(p.Version.Length > 0 && !p.Name.Contains(p.Version, StringComparison.OrdinalIgnoreCase) ? $" {p.Version}" : "")}"
            + (p.Publisher.Length > 0 ? $" ({p.Publisher})" : "")
            + $", size {(p.SizeBytes is long s ? ByteFormatter.FormatBytes(s) : "unknown")}"
            + $", installed {p.InstallDate?.ToString("d") ?? "unknown"}");
        var more = list.Count > maxResults ? $"{Environment.NewLine}... {list.Count - maxResults} more" : "";
        return $"{list.Count} programs:{Environment.NewLine}" + Lines(lines) + more;
    });

    [Description("Lists programs registered to start with Windows (registry Run and RunOnce keys). Read-only.")]
    public static string GetStartupPrograms() => Run(() =>
    {
        var entries = StartupPrograms.GetEntries();
        if (entries.Count == 0)
            return "(no startup programs found)";
        return Lines(entries.Select(e =>
        {
            var command = e.Command.Length <= 300 ? e.Command : e.Command[..300] + "...";
            return $"{e.Name} [{e.Location}]: {command}";
        }));
    });

    [Description("Lists the local user accounts on this PC: name, full name, enabled/disabled/locked, administrator or not, "
        + "last logon, password last set and description. Read-only.")]
    public static string GetLocalUsers() => Run(() =>
    {
        var accounts = LocalUsers.GetAccounts();
        if (accounts.Count == 0)
            return "(no local user accounts found)";
        return Lines(accounts.Take(100).Select(a =>
            $"{a.Name}{(a.FullName.Length > 0 ? $" ({a.FullName})" : "")}: {LocalUsers.FormatStatus(a)}, {LocalUsers.FormatType(a)}, "
            + $"last logon {a.LastLogon?.ToString("g") ?? "never"}, "
            + $"password set {a.PasswordLastSet?.ToString("g") ?? "unknown"}{(a.PasswordNeverExpires ? " (never expires)" : "")}"
            + (a.Description.Length > 0 ? $", \"{a.Description}\"" : "")));
    });

    [Description("Lists network adapters with type, status, IPv4/IPv6 addresses and MAC address.")]
    public static string GetNetworkAdapters() => Run(() =>
    {
        var adapters = NetworkInfo.GetAdapters();
        if (adapters.Count == 0)
            return "(no network adapters)";
        return Lines(adapters.Select(a =>
            $"{a.Name} ({a.Type}, {a.Status})"
            + (a.IPv4.Length > 0 ? $" IPv4 {a.IPv4}" : "")
            + (a.IPv6.Length > 0 ? $" IPv6 {a.IPv6}" : "")
            + (a.Mac.Length > 0 ? $" MAC {a.Mac}" : "")));
    });

    [Description("Pings a host name or IP address to check connectivity and latency (ICMP echo, 3 s timeout each).")]
    public static Task<string> PingHost(
        [Description("Host name or IP address, e.g. 8.8.8.8 or example.com.")] string host,
        [Description("Number of pings (1-4).")] int count = 4,
        CancellationToken cancellationToken = default) => RunAsync(
        () => RequireHost(host).Bind(h => PingAsync(h, Math.Clamp(count, 1, 4), cancellationToken)),
        ex => $"Ping failed: {ex.GetBaseException().Message}");

    private static async Task<Result<string>> PingAsync(string host, int count, CancellationToken cancellationToken)
    {
        var results = await NetworkInfo.PingAsync(host, count, cancellationToken);
        var ok = results.Where(r => r.Success).ToList();
        var summary = $"{ok.Count}/{results.Count} replies"
            + (ok.Count > 0 ? $", avg {ok.Average(r => r.RoundtripMs):0} ms" : "");
        return summary + Environment.NewLine + Lines(results.Select(r =>
            r.Success ? $"Reply from {r.Address}: time={r.RoundtripMs}ms" : $"Request failed: {r.Status}"));
    }

    [Description("Lists TCP/UDP ports opened by enabled inbound Windows Firewall allow rules. Read-only.")]
    public static string GetFirewallPorts()
    {
        if (!OperatingSystem.IsWindows())
            return "Error: Windows Firewall is only available on Windows.";
        return Run(DescribeFirewallPorts, ex => $"Error: {ex.GetBaseException().Message}");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Result<string> DescribeFirewallPorts()
    {
        var rules = FirewallRules.GetInboundAllowRules();
        if (rules.Count == 0)
            return "(no enabled inbound allow rules with TCP/UDP ports)";
        var more = rules.Count > 40 ? $"{Environment.NewLine}... {rules.Count - 40} more" : "";
        return Lines(rules.Take(40).Select(r => $"{r.Protocol,-4} {r.LocalPorts,-24} {r.Name}")) + more;
    }

    [Description("Traces the network path to a host over TCP, hop by hop with latency. Needs Administrator.")]
    public static Task<string> TraceTcpRoute(
        [Description("Host name or IP address, e.g. example.com.")] string host,
        [Description("TCP port to probe (1-65535).")] int port = 80,
        [Description("Maximum hops to trace (1-64).")] int maxHops = 10,
        [Description("Resolve each hop's IP address to a host name (slower).")] bool resolveHostNames = true,
        [Description("Per-probe timeout in milliseconds (500-10000).")] int timeoutMs = 2000,
        CancellationToken cancellationToken = default) => RunAsync(
        () => RequireHost(host).Bind(h => TraceAsync(h.Trim(), Math.Clamp(port, 1, 65535), Math.Clamp(maxHops, 1, 64),
            Math.Clamp(timeoutMs, 500, 10000), resolveHostNames, cancellationToken)),
        ex => $"Traceroute failed: {ex.GetBaseException().Message}");

    private static async Task<Result<string>> TraceAsync(
        string host, int port, int maxHops, int timeoutMs, bool resolveHostNames, CancellationToken cancellationToken)
    {
        var hops = await TcpTraceroute.TraceAsync(host, port, maxHops, timeoutMs, resolveHostNames, progress: null,
            cancellationToken: cancellationToken);
        if (hops.Count == 0)
            return "(no hops)";
        return Lines(hops.Select(h =>
            $"{h.Ttl,2}  {(h.Address ?? "*"),-15} {(h.HostName ?? ""),-30} {FormatRtt(h.Rtt1Ms),-8} {FormatRtt(h.Rtt2Ms),-8} {FormatRtt(h.Rtt3Ms),-8} {h.Note ?? ""}".TrimEnd()));
    }

    private static string FormatRtt(long? ms) => ms.HasValue ? $"{ms.Value} ms" : "*";

    internal static string? ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var key = directory.Trim().Trim('"');
        var candidate = key.ToLowerInvariant() switch
        {
            "documents" or "mydocuments" or "my documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "userprofile" or "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => ToLocalPath(key),
        };
        return candidate is not null && Directory.Exists(candidate) ? candidate : null;
    }

    // The model chooses tool paths, so they are untrusted. Only paths on known-local drives
    // (C:\...) are allowed: touching a UNC (\\host\share) or device path (\\?\, \\.\, \??\) makes
    // Windows connect to that host over SMB/WebDAV and send the user's NTLM credentials, and a
    // mapped drive letter points at the same kind of share. The lexical check runs first so no
    // file-system call happens for those; the root's drive type is then read from the system.
    internal static string? ToLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception)
        {
            return null;
        }
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            return null;
        return IsKnownLocalDrive(full[..3]) ? full : null;
    }

    // Mapped drives must be refused even when they look like ordinary letters. Only drive types
    // that stay on this machine pass; a subst drive reports itself as Fixed and can't be detected.
    private static bool IsKnownLocalDrive(string driveRoot)
    {
        try
        {
            return new DriveInfo(driveRoot).DriveType
                is not (DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown);
        }
        catch (Exception)
        {
            return true;
        }
    }

    [Description("Reads a text file. Read-only. The user is asked to allow each call.")]
    public static string ReadTextFile(
        [Description("Full file path.")] string path,
        [Description("Maximum characters to return (max 20000).")] int maxChars = 8000) => Run(() =>
        RequireLocalFile(path).Map(local => ReadStart(local, Math.Clamp(maxChars, 1, 20000))));

    // Read only what will be returned: an approved multi-GB log must not be pulled into
    // memory in full just to show its first 20,000 characters.
    private static string ReadStart(string path, int maxChars)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[maxChars];
        var read = reader.Read(buffer, 0, maxChars);
        var text = new string(buffer, 0, read);
        return reader.Peek() >= 0 ? text + "\n...(truncated)" : text;
    }

    [Description("Gets the value of an environment variable. The user is asked to allow each call.")]
    public static string GetEnvironmentVariable(
        [Description("Variable name, e.g. PATH.")] string name) => Run(() =>
        Environment.GetEnvironmentVariable(name) switch
        {
            null => "(not set)",
            var value => value.Length <= 2000 ? value : value[..2000] + "\n...(truncated)",
        });

    [Description("Lists environment variables (NAME=value) for a target: User, Machine or Process. Read-only. "
        + "The user is asked to allow this call because the values can contain secrets.")]
    public static string ListEnvironmentVariables(
        [Description("Target: User, Machine or Process (default Process).")] string target = "Process",
        [Description("Maximum variables to return (1-500).")] int maxResults = 200) => Run(() =>
    {
        var parsed = target.Trim().ToLowerInvariant() switch
        {
            "user" => EnvironmentVariableTarget.User,
            "machine" or "system" => EnvironmentVariableTarget.Machine,
            _ => EnvironmentVariableTarget.Process,
        };
        maxResults = Math.Clamp(maxResults, 1, 500);

        var entries = Environment.GetEnvironmentVariables(parsed)
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => $"{e.Key}={e.Value}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
            return $"(no {parsed} environment variables)";

        var lines = entries.Take(maxResults)
            .Select(s => s.Length <= 2000 ? s : s[..2000] + "...(truncated)");
        var more = entries.Count > maxResults ? $"{Environment.NewLine}... {entries.Count - maxResults} more" : "";
        return Lines(lines) + more;
    });

    [Description("Computes the hash of a file. Read-only.")]
    public static string ComputeFileHash(
        [Description("Full file path.")] string path,
        [Description("Algorithm: MD5, SHA-1, SHA-256, or SHA-512.")] string algorithm = "SHA-256") => Run(() =>
        RequireLocalFile(path).Bind(local =>
            RequireAlgorithm(algorithm).Bind(parsed =>
                FileHasher.ComputeHashes(local, [parsed])
                    .Map(hashes => $"{DisplayName(parsed)}: {hashes[parsed]}"))));

    [Description("Checks whether a file matches an expected hash. Picks the algorithm from the hash length.")]
    public static string VerifyFileHash(
        [Description("Full file path.")] string path,
        [Description("Expected hash in hex (MD5=32 chars, SHA-1=40, SHA-256=64, SHA-512=128).")] string expectedHash) => Run(() =>
        RequireLocalFile(path).Bind(local =>
        {
            var expected = expectedHash.Trim().ToLowerInvariant();
            return AlgorithmFromLength(expected).Bind(parsed =>
                FileHasher.ComputeHashes(local, [parsed]).Map(hashes =>
                    FileHasher.FindMatch(hashes, expected) is not null
                        ? $"MATCH ({DisplayName(parsed)})"
                        : $"NO MATCH ({DisplayName(parsed)}). Expected {expected}, got {hashes[parsed]}."));
        }));

    // ---- Railway plumbing -------------------------------------------------------------------

    // Exit of every tool: success is the text itself, a failure is its first error's message.
    // Errors that wrap an exception (ExceptionalError, e.g. an unreadable file from FileHasher)
    // read "Error: ..." like an exception caught here; check failures keep their own wording.
    // Cancellation is not a failure: it propagates so the chat can cancel the whole request.
    private static string Run(Func<Result<string>> tool, Func<Exception, string>? describeException = null)
    {
        try
        {
            return Render(tool());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (describeException ?? DescribeException)(ex);
        }
    }

    private static async Task<string> RunAsync(Func<Task<Result<string>>> tool, Func<Exception, string> describeException)
    {
        try
        {
            return Render(await tool());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return describeException(ex);
        }
    }

    private static string Render(Result<string> result) =>
        result.IsSuccess ? result.Value : Describe(result.Errors[0]);

    private static string Describe(IError error) =>
        error is ExceptionalError ? $"Error: {error.Message}" : error.Message;

    private static string DescribeException(Exception ex) => $"Error: {ex.Message}";

    private static string Lines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    private static string OrDefault(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static Result<string> RequireDirectory(string directory) =>
        ResolveDirectory(directory) is { } resolved
            ? resolved
            : Result.Fail<string>($"Directory not found (only local drive paths are allowed): {directory}");

    private static Result<string> RequireLocalFile(string path)
    {
        if (ToLocalPath(path) is not { } local)
            return Result.Fail<string>(
                $"Only local drive paths (e.g. C:\\Temp\\file.txt) are allowed; network and device paths are blocked: {path}");
        return File.Exists(local) ? local : Result.Fail<string>($"File not found: {path}");
    }

    private static Result<string> RequireHost(string host) =>
        string.IsNullOrWhiteSpace(host) || host.Trim().Length > 253
            ? Result.Fail<string>("Enter a host name or IP address.")
            : host;

    // A scan that hit an error on its (single) root fails as a whole; the tools pass one root.
    private static Result<(IReadOnlyList<T> Items, long Scanned)> WithoutScanErrors<T>(
        (IReadOnlyList<T> Items, long Scanned, IReadOnlyList<ScanError> Errors) scan)
    {
        if (scan.Errors.Count > 0)
            return Result.Fail($"Error: {scan.Errors[0].Message}");
        return (scan.Items, scan.Scanned);
    }

    private static Result<HashAlgorithmName> RequireAlgorithm(string algorithm)
    {
        if (ParseAlgorithm(algorithm) is { } parsed)
            return parsed;
        return Result.Fail<HashAlgorithmName>($"Unknown algorithm '{algorithm}'. Use MD5, SHA-1, SHA-256, or SHA-512.");
    }

    private static Result<HashAlgorithmName> AlgorithmFromLength(string expected)
    {
        if (expected.Length == 32)
            return HashAlgorithmName.MD5;
        if (expected.Length == 40)
            return HashAlgorithmName.SHA1;
        if (expected.Length == 64)
            return HashAlgorithmName.SHA256;
        if (expected.Length == 128)
            return HashAlgorithmName.SHA512;
        return Result.Fail<HashAlgorithmName>($"Cannot tell the algorithm from a {expected.Length}-character hash.");
    }

    // NOTE: plain if-chain (not a switch expression with a `_ => null` arm). Roslyn compiles
    // the null arm of a nullable-struct switch through the struct's implicit string operator
    // with a null string, which throws ArgumentNullException.
    private static HashAlgorithmName? ParseAlgorithm(string algorithm)
    {
        var key = algorithm.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
        if (key == "MD5")
            return HashAlgorithmName.MD5;
        if (key == "SHA1")
            return HashAlgorithmName.SHA1;
        if (key == "SHA256")
            return HashAlgorithmName.SHA256;
        if (key == "SHA512")
            return HashAlgorithmName.SHA512;
        return null;
    }

    private static string DisplayName(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5)
            return "MD5";
        if (algorithm == HashAlgorithmName.SHA1)
            return "SHA-1";
        if (algorithm == HashAlgorithmName.SHA256)
            return "SHA-256";
        if (algorithm == HashAlgorithmName.SHA384)
            return "SHA-384";
        if (algorithm == HashAlgorithmName.SHA512)
            return "SHA-512";
        return algorithm.Name ?? "Unknown";
    }
}
