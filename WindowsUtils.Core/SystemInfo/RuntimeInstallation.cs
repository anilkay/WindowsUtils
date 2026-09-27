using System.Diagnostics;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>An installed language runtime (Java, Python, Node.js) found on this PC.</summary>
/// <param name="Version">e.g. "21.0.4", or "Unknown" if it cannot be read.</param>
/// <param name="Vendor">e.g. "Eclipse Adoptium", or "" if unknown.</param>
/// <param name="Home">Installation folder.</param>
/// <param name="Source">Where it was found, e.g. JAVA_HOME, PATH, Registry, nvm.</param>
public sealed record RuntimeInstallation(string Version, string Vendor, string Home, string Source)
{
    /// <summary>Short display text, e.g. "21.0.4 (Eclipse Adoptium)".</summary>
    public string Display => Vendor.Length > 0 ? $"{Version} ({Vendor})" : Version;
}

/// <summary>
/// Shared helpers for the runtime detectors. They only read the file system, registry and
/// environment; they never start the runtime itself.
/// </summary>
internal static class RuntimeProbe
{
    /// <summary>Only local drive paths: probing a UNC path can hang or send the user's credentials.</summary>
    public static bool IsLocalDrivePath(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    /// <summary>Full path without a trailing slash, or null if it is not a local drive path.</summary>
    public static string? LocalFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\');
            return IsLocalDrivePath(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Follows symlinks and junctions (nvm, Oracle javapath) to the real folder.</summary>
    public static string ResolveDirectory(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName.TrimEnd('\\') ?? dir;
        }
        catch (Exception)
        {
            return dir;
        }
    }

    /// <summary>Full paths of every <paramref name="exeName"/> found on PATH, in PATH order.</summary>
    public static IEnumerable<string> FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            yield break;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = LocalFullPath(entry);
            if (dir is null)
                continue;
            string? found = null;
            try
            {
                var exe = Path.Combine(dir, exeName);
                if (File.Exists(exe))
                    found = new FileInfo(exe).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? exe;
            }
            catch (Exception)
            {
                // Unreadable PATH entry - skip it.
            }
            if (found is not null)
                yield return found;
        }
    }

    /// <summary>Subfolders of <paramref name="dir"/>, or none if it does not exist or cannot be read.</summary>
    public static IEnumerable<string> SubDirectories(string? dir)
    {
        var local = LocalFullPath(dir);
        if (local is null || !Directory.Exists(local))
            return [];
        try
        {
            return Directory.GetDirectories(local);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Product version of an executable (e.g. "20.11.1"), or null.</summary>
    public static string? FileProductVersion(string exe, out string company)
    {
        company = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            company = info.CompanyName?.Trim() ?? "";
            var version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Sort key for version strings such as "1.8.0_402", "21.0.4", "v20.11.1" or "3.12.4".
    /// Java's legacy "1.x" numbering is mapped to "x".
    /// </summary>
    public static Version SortKey(string version)
    {
        var parts = version.TrimStart('v', 'V').Split(['.', '_', '+', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), out var n) ? n : 0)
            .Concat([0, 0, 0, 0])
            .Take(4)
            .ToArray();
        if (parts[0] == 1 && parts[1] >= 2)
            parts = [parts[1], 0, parts[2], parts[3]]; // 1.8.0_402 -> 8.0.0.402
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>Collects installs, skipping folders already seen (compared after resolving links).</summary>
    public sealed class Collector
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public List<RuntimeInstallation> Items { get; } = [];

        /// <summary>Adds the install if its folder is new; <paramref name="home"/> must already be validated.</summary>
        public bool TryAdd(string home, Func<string, RuntimeInstallation> describe)
        {
            if (!_seen.Add(ResolveDirectory(home)))
                return false;
            Items.Add(describe(home));
            return true;
        }

        /// <summary>Sorts the items from <paramref name="start"/> on by version, newest first.</summary>
        public void SortNewestFirst(int start)
        {
            var tail = Items.Skip(start).OrderByDescending(i => SortKey(i.Version)).ToList();
            Items.RemoveRange(start, Items.Count - start);
            Items.AddRange(tail);
        }
    }
}
