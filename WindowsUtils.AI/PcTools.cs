using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
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
/// Every method catches its own errors and returns them as text.
/// </summary>
public static class PcTools
{
    // Concrete OpenAI client (not IChatClient): the 7-argument AsAIAgent overload
    // used by ChatSession is defined on this type.
    internal static ChatClient CreateChatClient(string model, string apiKey, Uri endpoint) =>
        new(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });

    [Description("Gets the current date and time on this PC.")]
    public static string GetCurrentTime()
    {
        try
        {
            return DateTime.Now.ToString("F");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets basic system info: machine name, user, OS, CPU count, .NET runtime.")]
    public static string GetSystemInfo()
    {
        try
        {
            return $"Machine: {Environment.MachineName}\n"
                + $"User: {Environment.UserName}\n"
                + $"OS: {Environment.OSVersion} (64-bit OS: {Environment.Is64BitOperatingSystem})\n"
                + $"Processors: {Environment.ProcessorCount}\n"
                + $"Runtime: {Environment.Version}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

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

    private static string DescribeRuntimes(Func<IReadOnlyList<RuntimeInstallation>> find, string name, string searched)
    {
        try
        {
            var installs = find();
            if (installs.Count == 0)
                return $"{name} is not installed (nothing found via {searched}).";
            return string.Join(Environment.NewLine, installs.Take(20).Select((r, i) =>
                $"{(i == 0 ? "Default: " : "")}{r.Display} at {r.Home} [{r.Source}]"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the top running processes by memory usage.")]
    public static string GetProcesses(
        [Description("Maximum number of processes to return (1-30).")] int maxProcesses = 10)
    {
        try
        {
            maxProcesses = Math.Clamp(maxProcesses, 1, 30);
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
            return string.Join(Environment.NewLine,
                rows.OrderByDescending(r => r.Mb).Take(maxProcesses).Select(r => $"{r.Name} (Id {r.Id}, {r.Mb} MB)"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists ready drives with free/total space.")]
    public static string GetDrives()
    {
        try
        {
            var rows = DriveInfo.GetDrives().Select(d =>
            {
                if (!d.IsReady)
                    return $"{d.Name} ({d.DriveType}, not ready)";
                return $"{d.Name} ({d.DriveType}, {d.VolumeLabel}) free {d.AvailableFreeSpace / 1024 / 1024 / 1024} GB of {d.TotalSize / 1024 / 1024 / 1024} GB";
            });
            return string.Join(Environment.NewLine, rows);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists files and folders in a directory. Read-only.")]
    public static string ListFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory,
        [Description("Search pattern, e.g. *.log.")] string pattern = "*.*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (only local drive paths are allowed): {directory}";
            maxResults = Math.Clamp(maxResults, 1, 100);
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "*.*";
            var entries = Directory.EnumerateFileSystemEntries(resolved, pattern).Take(maxResults).ToArray();
            return entries.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists subfolders in a directory. Read-only.")]
    public static string ListFolders(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory,
        [Description("Search pattern, e.g. Proj*.")] string pattern = "*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (only local drive paths are allowed): {directory}";
            maxResults = Math.Clamp(maxResults, 1, 100);
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "*";
            var entries = Directory.EnumerateDirectories(resolved, pattern).Take(maxResults).ToArray();
            return entries.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the largest files in a folder, sorted by size descending. Use for 'biggest files' questions.")]
    public static string GetLargestFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory = "Documents",
        [Description("How many files to return (1-50).")] int top = 10,
        [Description("Search subfolders too.")] bool recursive = true)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (only local drive paths are allowed): {directory}";
            top = Math.Clamp(top, 1, 50);

            var (files, _, errors) = FileScanner.FindLargestFiles([resolved], top, recursive: recursive);
            if (errors.Count > 0)
                return $"Error: {errors[0].Message}";
            if (files.Count == 0)
                return "(empty)";
            return string.Join(Environment.NewLine,
                files.Select(f => $"{ByteFormatter.FormatBytes(f.Size),-10} {f.FullPath}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Finds duplicate files (identical content, confirmed by SHA-256) in a folder and its subfolders. "
        + "Returns groups sorted by space that could be freed. Read-only: it never deletes anything.")]
    public static string FindDuplicateFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads, UserProfile.")] string directory = "Documents",
        [Description("Ignore files smaller than this many megabytes (0 = any size).")] int minSizeMb = 1,
        [Description("Maximum number of duplicate groups to return (1-50).")] int maxGroups = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (only local drive paths are allowed): {directory}";
            maxGroups = Math.Clamp(maxGroups, 1, 50);
            var minSize = Math.Max(0, minSizeMb) * 1024L * 1024L;

            var (groups, scanned, errors) = DuplicateFinder.FindDuplicates([resolved], minSize, cancellationToken: cancellationToken);
            if (errors.Count > 0)
                return $"Error: {errors[0].Message}";
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
            return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            throw; // let the chat cancel the whole request
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Shows what takes up space inside a folder: its direct subfolders (with total size) and files, largest first. Read-only.")]
    public static string GetFolderSizes(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads, UserProfile.")] string directory = "Documents",
        [Description("Maximum items to return (1-50).")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found (only local drive paths are allowed): {directory}";
            top = Math.Clamp(top, 1, 50);

            var items = FileScanner.GetFolderContents(resolved, cancellationToken);
            if (items.Count == 0)
                return "(empty)";
            var lines = new List<string>
            {
                $"{resolved}: {items.Count} items, total {ByteFormatter.FormatBytes(items.Sum(i => i.Size))}"
                    + (items.Count > top ? $". Showing the largest {top}." : "."),
            };
            lines.AddRange(items.Take(top).Select(i =>
                $"{ByteFormatter.FormatBytes(i.Size),-10} {(i.IsFolder ? "[folder]" : "[file]  ")} {i.Name}"));
            return string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists programs registered to start with Windows (registry Run and RunOnce keys). Read-only.")]
    public static string GetStartupPrograms()
    {
        try
        {
            var entries = StartupPrograms.GetEntries();
            if (entries.Count == 0)
                return "(no startup programs found)";
            return string.Join(Environment.NewLine, entries.Select(e =>
            {
                var command = e.Command.Length <= 300 ? e.Command : e.Command[..300] + "...";
                return $"{e.Name} [{e.Location}]: {command}";
            }));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the local user accounts on this PC: name, full name, enabled/disabled/locked, administrator or not, "
        + "last logon, password last set and description. Read-only.")]
    public static string GetLocalUsers()
    {
        try
        {
            var accounts = LocalUsers.GetAccounts();
            if (accounts.Count == 0)
                return "(no local user accounts found)";
            return string.Join(Environment.NewLine, accounts.Take(100).Select(a =>
                $"{a.Name}{(a.FullName.Length > 0 ? $" ({a.FullName})" : "")}: {LocalUsers.FormatStatus(a)}, {LocalUsers.FormatType(a)}, "
                + $"last logon {a.LastLogon?.ToString("g") ?? "never"}, "
                + $"password set {a.PasswordLastSet?.ToString("g") ?? "unknown"}{(a.PasswordNeverExpires ? " (never expires)" : "")}"
                + (a.Description.Length > 0 ? $", \"{a.Description}\"" : "")));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists network adapters with type, status, IPv4/IPv6 addresses and MAC address.")]
    public static string GetNetworkAdapters()
    {
        try
        {
            var adapters = NetworkInfo.GetAdapters();
            if (adapters.Count == 0)
                return "(no network adapters)";
            return string.Join(Environment.NewLine, adapters.Select(a =>
                $"{a.Name} ({a.Type}, {a.Status})"
                + (a.IPv4.Length > 0 ? $" IPv4 {a.IPv4}" : "")
                + (a.IPv6.Length > 0 ? $" IPv6 {a.IPv6}" : "")
                + (a.Mac.Length > 0 ? $" MAC {a.Mac}" : "")));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Pings a host name or IP address to check connectivity and latency (ICMP echo, 3 s timeout each).")]
    public static async Task<string> PingHost(
        [Description("Host name or IP address, e.g. 8.8.8.8 or example.com.")] string host,
        [Description("Number of pings (1-4).")] int count = 4,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host) || host.Trim().Length > 253)
                return "Enter a host name or IP address.";
            var results = await NetworkInfo.PingAsync(host, Math.Clamp(count, 1, 4), cancellationToken);
            var ok = results.Where(r => r.Success).ToList();
            var summary = $"{ok.Count}/{results.Count} replies"
                + (ok.Count > 0 ? $", avg {ok.Average(r => r.RoundtripMs):0} ms" : "");
            return summary + Environment.NewLine + string.Join(Environment.NewLine, results.Select(r =>
                r.Success ? $"Reply from {r.Address}: time={r.RoundtripMs}ms" : $"Request failed: {r.Status}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Ping failed: {ex.GetBaseException().Message}";
        }
    }

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

    // The model chooses tool paths, so they are untrusted. Only drive-letter paths (C:\...)
    // are allowed: touching a UNC (\\host\share) or device path (\\?\, \\.\, \??\) makes
    // Windows connect to that host over SMB/WebDAV and send the user's NTLM credentials.
    // Checked before any file-system call; Path.GetFullPath is purely lexical.
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
        return full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == '\\'
            ? full
            : null;
    }

    private static string LocalOnlyMessage(string path) =>
        $"Only local drive paths (e.g. C:\\Temp\\file.txt) are allowed; network and device paths are blocked: {path}";

    [Description("Reads a text file. Read-only.")]
    public static string ReadTextFile(
        [Description("Full file path.")] string path,
        [Description("Maximum characters to return (max 20000).")] int maxChars = 8000)
    {
        try
        {
            var local = ToLocalPath(path);
            if (local is null)
                return LocalOnlyMessage(path);
            if (!File.Exists(local))
                return $"File not found: {path}";
            maxChars = Math.Clamp(maxChars, 1, 20000);
            var text = File.ReadAllText(local);
            return text.Length <= maxChars ? text : text[..maxChars] + "\n...(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets the value of an environment variable.")]
    public static string GetEnvironmentVariable(
        [Description("Variable name, e.g. PATH.")] string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
                return "(not set)";
            return value.Length <= 2000 ? value : value[..2000] + "\n...(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Computes the hash of a file. Read-only.")]
    public static string ComputeFileHash(
        [Description("Full file path.")] string path,
        [Description("Algorithm: MD5, SHA-1, SHA-256, or SHA-512.")] string algorithm = "SHA-256")
    {
        try
        {
            var local = ToLocalPath(path);
            if (local is null)
                return LocalOnlyMessage(path);
            if (!File.Exists(local))
                return $"File not found: {path}";
            var parsed = ParseAlgorithm(algorithm);
            if (parsed is null)
                return $"Unknown algorithm '{algorithm}'. Use MD5, SHA-1, SHA-256, or SHA-512.";
            var hashes = FileHasher.ComputeHashes(local, [parsed.Value]);
            return $"{DisplayName(parsed.Value)}: {hashes[parsed.Value]}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Checks whether a file matches an expected hash. Picks the algorithm from the hash length.")]
    public static string VerifyFileHash(
        [Description("Full file path.")] string path,
        [Description("Expected hash in hex (MD5=32 chars, SHA-1=40, SHA-256=64, SHA-512=128).")] string expectedHash)
    {
        try
        {
            var local = ToLocalPath(path);
            if (local is null)
                return LocalOnlyMessage(path);
            if (!File.Exists(local))
                return $"File not found: {path}";
            var expected = expectedHash.Trim().ToLowerInvariant();
            HashAlgorithmName? parsed = null;
            if (expected.Length == 32)
                parsed = HashAlgorithmName.MD5;
            else if (expected.Length == 40)
                parsed = HashAlgorithmName.SHA1;
            else if (expected.Length == 64)
                parsed = HashAlgorithmName.SHA256;
            else if (expected.Length == 128)
                parsed = HashAlgorithmName.SHA512;
            if (parsed is null)
                return $"Cannot tell the algorithm from a {expected.Length}-character hash.";
            var hashes = FileHasher.ComputeHashes(local, [parsed.Value]);
            var name = DisplayName(parsed.Value);
            return FileHasher.FindMatch(hashes, expected) is not null
                ? $"MATCH ({name})"
                : $"NO MATCH ({name}). Expected {expected}, got {hashes[parsed.Value]}.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
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
