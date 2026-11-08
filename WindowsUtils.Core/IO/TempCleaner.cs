using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using FluentResults;
using Microsoft.Win32.SafeHandles;

namespace WindowsUtils.Core.IO;

/// <summary>A fixed, known-safe place for temporary or cache files. <paramref name="Pattern"/> limits
/// which files count (e.g. <c>thumbcache_*.db</c>); <paramref name="Recursive"/> includes subfolders.</summary>
public sealed record CleanupLocation(string Name, string Description, IReadOnlyList<string> Roots, string Pattern = "*", bool Recursive = true);

/// <summary>Size of a <see cref="CleanupLocation"/>: everything in it, and the part old enough to be cleaned.</summary>
public sealed record CleanupSize(CleanupLocation Location, long Files, long Bytes, long CleanableFiles, long CleanableBytes);

/// <summary>Outcome of <see cref="TempCleaner.Clean"/>. Skipped files were recent, in use, read-only,
/// access denied, or no longer inside the location's folder.</summary>
public sealed record CleanupResult(long DeletedFiles, long FreedBytes, long SkippedFiles);

/// <summary>
/// Temp and cache cleaner limited to a fixed list of safe locations (temp folders, browser and thumbnail
/// caches, error reports, crash dumps). <see cref="Clean"/> only accepts folders that <see cref="GetLocations"/>
/// returns right now. Only files untouched for <see cref="MinimumAge"/> are deleted, so files a running program
/// just wrote stay; files in use are skipped. Reparse points (junctions, symlinks) are never followed: each file
/// is opened, checked to still lie inside its cleanup folder, and deleted through that same handle.
/// Deletion is permanent.
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
        var userTemp = Path.GetTempPath();

        var candidates = new List<CleanupLocation>
        {
            // %TEMP% is a user setting: it is only cleaned when it looks like a temp folder, so a TEMP
            // pointing at Documents or D:\Projects can never get its files deleted.
            new("User Temp", "Your temporary files (%TEMP%).", IsTempFolder(userTemp, local) ? [userTemp] : []),
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
        foreach (var file in EnumerateFiles(location.Roots, location.Pattern, location.Recursive))
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
    /// then removes subfolders left empty. Files that cannot be deleted are counted as skipped. Fails when
    /// none of the location's folders is a current, safe cleanup folder (then nothing is touched).
    /// </summary>
    public static Result<CleanupResult> Clean(CleanupLocation location, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Result.Fail("Temp cleaning is only available on Windows.");
        return ResolveRoots(location)
            .Map(roots => CleanRoots(roots, location, cancellationToken));
    }

    // A folder measured earlier may have been swapped for a junction since, and a caller may build its
    // own location: only roots that GetLocations returns right now (which re-runs IsSafeRoot) are used,
    // each pinned to its final path so every file can be checked against it.
    private static Result<IReadOnlyList<CleanupRoot>> ResolveRoots(CleanupLocation location)
    {
        var known = GetLocations().SelectMany(l => l.Roots).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = location.Roots.Select(root => RequireKnownRoot(root, known).Bind(ResolveRoot)).ToList();
        var roots = resolved.Where(r => r.IsSuccess).Select(r => r.Value).ToList();
        if (roots.Count > 0)
            return roots;
        return Result.Fail($"{location.Name}: nothing was cleaned. "
            + (resolved.Count > 0 ? resolved[0].Errors[0].Message : "It has no folders."));
    }

    private static Result<string> RequireKnownRoot(string root, IReadOnlySet<string> known) =>
        known.Contains(root) ? root : Result.Fail<string>($"'{root}' is not a safe cleanup folder.");

    private static Result<CleanupRoot> ResolveRoot(string root)
    {
        // Read attributes only: the root itself is never deleted, and a non-admin may lack Delete on it.
        using var handle = Open(root, FileReadAttributes, FileFlagBackupSemantics, directory: true);
        if (handle.IsInvalid)
            return Win32Failure($"Could not open '{root}'");
        return FinalPath(handle).Map(final => new CleanupRoot(root, final));
    }

    private static CleanupResult CleanRoots(IReadOnlyList<CleanupRoot> roots, CleanupLocation location, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - MinimumAge;
        long deleted = 0, freed = 0, skipped = 0;
        foreach (var root in roots)
        {
            // Listed first so deleting does not disturb the enumeration.
            var candidates = new List<CacheFile>();
            foreach (var file in EnumerateFiles([root.Path], location.Pattern, location.Recursive))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ModifiedUtc < cutoff)
                    candidates.Add(file);
            }

            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = DeleteIfOld(file.Path, root, cutoff, directory: false);
                if (result.IsSuccess)
                {
                    deleted++;
                    freed += result.Value;
                }
                else
                {
                    skipped++;
                }
            }

            if (location.Recursive)
                RemoveEmptyFolders(root, cutoff, cancellationToken);
        }
        return new CleanupResult(deleted, freed, skipped);
    }

    // One file or folder, as a railway on a single handle: open it without following a final link →
    // its real path must still be inside the root (a folder above it may have been swapped for a
    // junction since the scan) → old enough, read from the handle → mark for deletion on that handle.
    // Files are opened sharing Delete only, so no program can rewrite one between the age check and the
    // deletion; Windows removes it when the handle closes. Returns the freed size.
    private static Result<long> DeleteIfOld(string path, CleanupRoot root, DateTime cutoffUtc, bool directory)
    {
        using var handle = Open(path, Delete | FileReadAttributes, FileFlagOpenReparsePoint | (directory ? FileFlagBackupSemantics : 0), directory);
        if (handle.IsInvalid)
            return Win32Failure($"Could not open '{path}'");
        return FinalPath(handle)
            .Bind(final => RequireInside(final, root))
            .Bind(() => RequireOld(handle, cutoffUtc, directory))
            .Bind(size => MarkForDeletion(handle).ToResult(size));
    }

    private static Result RequireInside(string finalPath, CleanupRoot root) =>
        Result.OkIf(finalPath.StartsWith(root.FinalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            () => $"'{finalPath}' is outside '{root.FinalPath}'.");

    // Files go by their last write, folders by their creation (a folder's write time changes whenever
    // a file in it is deleted). A folder frees no space of its own.
    private static Result<long> RequireOld(SafeFileHandle handle, DateTime cutoffUtc, bool directory)
    {
        try
        {
            var time = directory ? File.GetCreationTimeUtc(handle) : File.GetLastWriteTimeUtc(handle);
            if (time >= cutoffUtc)
                return Result.Fail<long>("Changed too recently.");
            return directory ? 0 : RandomAccess.GetLength(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<long>(new ExceptionalError(ex.Message, ex));
        }
    }

    // Fails for read-only files and for folders that are not empty, which is what keeps them.
    private static Result MarkForDeletion(SafeFileHandle handle)
    {
        var info = new FileDispositionInfo { DeleteFile = true };
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref info, (uint)Marshal.SizeOf<FileDispositionInfo>())
            ? Result.Ok()
            : Win32Failure("Could not delete");
    }

    private static Result<string> FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length) // too small: length is the size needed
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length)
            return Win32Failure("Could not resolve the real path");
        var path = new string(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : Result.Fail<string>($"'{path}' is not on a local drive.");
    }

    private static Result Win32Failure(string what)
    {
        var exception = new Win32Exception(Marshal.GetLastPInvokeError());
        return Result.Fail(new ExceptionalError($"{what}: {exception.Message}", exception));
    }

    private readonly record struct CleanupRoot(string Path, string FinalPath);

    private readonly record struct CacheFile(string Path, long Size, DateTime ModifiedUtc);

    private static IEnumerable<CacheFile> EnumerateFiles(IEnumerable<string> roots, string pattern, bool recursive)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            var files = new FileSystemEnumerable<CacheFile>(root,
                (ref FileSystemEntry entry) => new CacheFile(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
                CreateOptions(recursive))
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
    // Folders created recently are kept for the same reason recent files are; folders that are not
    // empty fail to be marked for deletion and stay as well.
    private static void RemoveEmptyFolders(CleanupRoot root, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root.Path))
            return;
        var folders = new List<string>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root.Path, "*", CreateOptions(recursive: true)))
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
            _ = DeleteIfOld(folder, root, cutoffUtc, directory: true); // in use, not empty or recent: kept
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

    // %TEMP% counts as a temp folder when it is %LOCALAPPDATA%\Temp (or a folder below it, as in
    // remote sessions) or its own name is Temp/Tmp, e.g. D:\Temp.
    private static bool IsTempFolder(string path, string localAppData)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (localAppData.Length > 0)
            {
                var localTemp = Path.Combine(localAppData, "Temp");
                if (full.Equals(localTemp, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(localTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return Path.GetFileName(full) is { } name
                && (name.Equals("Temp", StringComparison.OrdinalIgnoreCase) || name.Equals("Tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
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

    // ---- Win32: open without following a final link, read the real path, delete by handle ----

    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    // Folders may be open elsewhere (Explorer), so they share everything; files share Delete only.
    private static SafeFileHandle Open(string path, uint access, uint flags, bool directory) =>
        CreateFile(path, access,
            directory ? FileShareRead | FileShareWrite | FileShareDelete : FileShareDelete,
            IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        ref FileDispositionInfo fileInformation, uint bufferSize);
}
