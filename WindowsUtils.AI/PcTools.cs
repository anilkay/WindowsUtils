using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using OpenAI;
using OpenAI.Chat;
using WindowsUtils.Core;
using WindowsUtils.Core.Hashing;
using WindowsUtils.Core.IO;

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
                return $"Directory not found: {directory}";
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
                return $"Directory not found: {directory}";
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
                return $"Directory not found: {directory}";
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
            _ => key,
        };
        return Directory.Exists(candidate) ? candidate : null;
    }

    [Description("Reads a text file. Read-only.")]
    public static string ReadTextFile(
        [Description("Full file path.")] string path,
        [Description("Maximum characters to return (max 20000).")] int maxChars = 8000)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return $"File not found: {path}";
            maxChars = Math.Clamp(maxChars, 1, 20000);
            var text = File.ReadAllText(path);
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return $"File not found: {path}";
            var parsed = ParseAlgorithm(algorithm);
            if (parsed is null)
                return $"Unknown algorithm '{algorithm}'. Use MD5, SHA-1, SHA-256, or SHA-512.";
            var hashes = FileHasher.ComputeHashes(path, [parsed.Value]);
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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
            var hashes = FileHasher.ComputeHashes(path, [parsed.Value]);
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
