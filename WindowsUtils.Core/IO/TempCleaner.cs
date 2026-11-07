using System.IO.Enumeration;

namespace WindowsUtils.Core.IO;

/// <summary>A fixed, known-safe place for temporary or cache files. <paramref name="Pattern"/> limits
/// which files count (e.g. <c>thumbcache_*.db</c>); <paramref name="Recursive"/> includes subfolders.</summary>
public sealed record CleanupLocation(string Name, string Description, IReadOnlyList<string> Roots, string Pattern = "*", bool Recursive = true);

/// <summary>Size of a <see cref="CleanupLocation"/>: everything in it, and the part old enough to be cleaned.</summary>
public sealed record CleanupSize(CleanupLocation Location, long Files, long Bytes, long CleanableFiles, long CleanableBytes);

/// <summary>Outcome of <see cref="TempCleaner.Clean"/>. Skipped files were in use, read-only or access denied.</summary>
public sealed record CleanupResult(long DeletedFiles, long FreedBytes, long SkippedFiles);

/// <summary>
/// Temp and cache cleaner limited to a fixed list of safe locations (temp folders, browser and thumbnail
/// caches, error reports, crash dumps). Paths never come from the caller, only from <see cref="GetLocations"/>.
/// Only files untouched for <see cref="MinimumAge"/> are deleted, so files a running program just wrote stay;
/// files in use are skipped. Reparse points (junctions, symlinks) are never followed. Deletion is permanent.
/// </summary>
public static class TempCleaner
{
    /// <summary>Files modified more recently than this are kept (an installer may still be using them).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    /// <summary>The safe locations that exist on this PC. Locations whose folders are all missing are left out.</summary>
    public static IReadOnlyList<CleanupLocation> GetLocations()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var candidates = new List<CleanupLocation>
        {
            new("User Temp", "Your temporary files (%TEMP%).", [Path.GetTempPath()]),
            new("Windows Temp", "System temporary files (needs Administrator to clean most of them).", [Path.Combine(windows, "Temp")]),
            new("Thumbnail Cache", "Explorer thumbnail cache; rebuilt automatically.",
                [Path.Combine(local, @"Microsoft\Windows\Explorer")], "thumbcache_*.db", Recursive: false),
            new("Edge Cache", "Microsoft Edge web cache (all profiles).", ChromiumCaches(Path.Combine(local, @"Microsoft\Edge\User Data"))),
            new("Chrome Cache", "Google Chrome web cache (all profiles).", ChromiumCaches(Path.Combine(local, @"Google\Chrome\User Data"))),
            new("Firefox Cache", "Mozilla Firefox web cache (all profiles).", Subfolders(Path.Combine(local, @"Mozilla\Firefox\Profiles"), "cache2")),
            new("Error Reports", "Windows Error Reporting archives and queues.",
                [Path.Combine(local, @"Microsoft\Windows\WER"), Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"),
                 Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue")]),
            new("Crash Dumps", "Memory dumps written when programs crash.", [Path.Combine(local, "CrashDumps")]),
        };

        return candidates
            .Select(c => c with { Roots = c.Roots.Where(IsSafeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList() })
            .Where(c => c.Roots.Count > 0)
            .ToList();
    }

    /// <summary>Measures a location without changing anything.</summary>
    public static CleanupSize Measure(CleanupLocation location, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - MinimumAge;
        long files = 0, bytes = 0, cleanableFiles = 0, cleanableBytes = 0;
        foreach (var file in EnumerateFiles(location))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files++;
            bytes += file.Size;
            if (file.ModifiedUtc < cutoff)
            {
                cleanableFiles++;
                cleanableBytes += file.Size;
            }
        }
        return new CleanupSize(location, files, bytes, cleanableFiles, cleanableBytes);
    }

    /// <summary>
    /// Permanently deletes the files in <paramref name="location"/> older than <see cref="MinimumAge"/>,
    /// then removes subfolders left empty. Files that cannot be deleted are counted as skipped.
    /// </summary>
    public static CleanupResult Clean(CleanupLocation location, CancellationToken cancellationToken = default)
    {
        // The roots are checked again here, not only in GetLocations: a caller may build its own
        // location, and a folder measured earlier may have been swapped for a junction since.
        location = location with { Roots = location.Roots.Where(IsSafeRoot).ToList() };
        var cutoff = DateTime.UtcNow - MinimumAge;
        long deleted = 0, freed = 0, skipped = 0;
        // Listed first so deleting does not disturb the enumeration.
        var candidates = new List<CacheFile>();
        foreach (var file in EnumerateFiles(location))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.ModifiedUtc < cutoff)
                candidates.Add(file);
        }

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteIfOld(file.Path, cutoff) is long bytes)
            {
                deleted++;
                freed += bytes;
            }
            else
            {
                skipped++;
            }
        }

        if (location.Recursive)
        {
            foreach (var root in location.Roots)
                RemoveEmptyFolders(root, cutoff, cancellationToken);
        }
        return new CleanupResult(deleted, freed, skipped);
    }

    // The age is read again from an open handle that lets no one else write the file (sharing
    // Delete only), so a program that rewrote it after the scan keeps it. Deleting through that
    // sharing mode marks the file; Windows removes it when the handle closes. Returns the freed
    // size, or null when the file is recent, in use, read-only or access is denied.
    private static long? TryDeleteIfOld(string path, DateTime cutoffUtc)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            if (File.GetLastWriteTimeUtc(stream.SafeFileHandle) >= cutoffUtc)
                return null;
            var size = stream.Length;
            File.Delete(path);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct CacheFile(string Path, long Size, DateTime ModifiedUtc);

    private static IEnumerable<CacheFile> EnumerateFiles(CleanupLocation location)
    {
        foreach (var root in location.Roots)
        {
            if (!Directory.Exists(root))
                continue;
            var pattern = location.Pattern;
            var files = new FileSystemEnumerable<CacheFile>(root,
                (ref FileSystemEntry entry) => new CacheFile(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
                CreateOptions(location.Recursive))
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && FileSystemName.MatchesSimpleExpression(pattern, entry.FileName),
            };

            IEnumerator<CacheFile> enumerator;
            try
            {
                enumerator = files.GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            using (enumerator)
            {
                while (true)
                {
                    try
                    {
                        if (!enumerator.MoveNext())
                            break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break; // root vanished or became unreadable mid-scan
                    }
                    yield return enumerator.Current;
                }
            }
        }
    }

    // Deepest first, so a parent emptied by removing its children goes too. The root itself stays.
    // Folders created recently are kept for the same reason recent files are.
    private static void RemoveEmptyFolders(string root, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return;
        var folders = new List<string>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", CreateOptions(recursive: true)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                folders.Add(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        folders.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.GetCreationTimeUtc(folder) < cutoffUtc && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use or access denied - keep it
            }
        }
    }

    private static EnumerationOptions CreateOptions(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    // Chromium keeps one folder per profile ("Default", "Profile 1", ...), each with a Cache and Code Cache.
    private static List<string> ChromiumCaches(string userData)
    {
        var caches = new List<string>();
        foreach (var profile in SafeSubdirectories(userData))
        {
            caches.Add(Path.Combine(profile, @"Cache\Cache_Data"));
            caches.Add(Path.Combine(profile, "Code Cache"));
        }
        return caches;
    }

    private static List<string> Subfolders(string parent, string child) =>
        SafeSubdirectories(parent).Select(p => Path.Combine(p, child)).ToList();

    private static IEnumerable<string> SafeSubdirectories(string parent)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, "*", CreateOptions(recursive: false)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Guards against a misconfigured TEMP (e.g. TEMP=C:\) or an empty special folder turning a cleanup
    // location into a drive root, the Windows folder or the user profile. The root must exist on a
    // local drive, and neither it nor any folder above it may be a junction or symlink: enumeration
    // skips reparse points below the root, but a link above it would point the whole root elsewhere.
    private static bool IsSafeRoot(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Length <= 3 || full[1] != ':' || full[2] != '\\')
                return false; // not a drive path, or the drive root itself

            string[] protectedFolders =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ];
            if (protectedFolders.Any(p => p.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(p), full, StringComparison.OrdinalIgnoreCase)))
                return false;

            var info = new DirectoryInfo(full);
            if (!info.Exists)
                return false;
            for (var folder = info; folder.Parent is not null; folder = folder.Parent)
            {
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            return new DriveInfo(full[..3]).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
