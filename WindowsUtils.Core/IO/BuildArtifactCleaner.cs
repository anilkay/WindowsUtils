using System.IO.Enumeration;
using FluentResults;

namespace WindowsUtils.Core.IO;

/// <summary>A build output folder (<c>bin</c> or <c>obj</c>) that belongs to a project file.</summary>
public sealed record BuildArtifact(string ProjectFile, string Folder, long Files, long Bytes);

/// <summary>Progress reported while looking for projects.</summary>
public sealed record BuildScanProgress(long ProjectsScanned, string CurrentProject);

/// <summary>
/// Result of <see cref="BuildArtifactCleaner.Scan"/>: the output folders found under <see cref="Root"/>,
/// how many project files were seen, and folders that could not be read.
/// </summary>
public sealed record BuildArtifactScan(
    string Root,
    IReadOnlyList<BuildArtifact> Artifacts,
    long ProjectsScanned,
    IReadOnlyList<ScanError> Errors)
{
    public long Files => Artifacts.Sum(a => a.Files);
    public long Bytes => Artifacts.Sum(a => a.Bytes);
}

/// <summary>Outcome of <see cref="BuildArtifactCleaner.Clean"/>.</summary>
public sealed record BuildCleanResult(long FoldersDeleted, long FilesDeleted, long FreedBytes, long FailedEntries);

/// <summary>
/// Finds and deletes the <c>bin</c> and <c>obj</c> output folders of .NET projects. A folder only counts
/// when it sits directly next to a project file (<c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>,
/// <c>.vcxproj</c>), and <see cref="Clean"/> checks that again before deleting. Reparse points
/// (junctions, symlinks) are never followed, so a link inside a repository can never point the deletion
/// outside it. Deletion is permanent; the next build recreates the folders.
/// </summary>
public static class BuildArtifactCleaner
{
    /// <summary>Project files whose output folders are cleaned.</summary>
    public static readonly IReadOnlyList<string> ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".vcxproj"];

    /// <summary>The output folder names looked for next to a project file.</summary>
    public static readonly IReadOnlyList<string> FolderNames = ["bin", "obj"];

    /// <summary>
    /// Where Visual Studio puts new repositories (<c>%USERPROFILE%\source\repos</c>), falling back to
    /// <c>source</c> or Documents when that folder does not exist.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0)
            {
                var repos = Path.Combine(profile, "source", "repos");
                if (Directory.Exists(repos))
                    return repos;
                var source = Path.Combine(profile, "source");
                if (Directory.Exists(source))
                    return source;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    /// <summary>
    /// Finds every project file under <paramref name="root"/> (recursively, skipping junctions, hidden
    /// system folders and inaccessible paths) and measures the <c>bin</c> and <c>obj</c> folders next to
    /// it. Fails when the folder does not exist. Nothing is changed.
    /// </summary>
    public static Result<BuildArtifactScan> Scan(
        string root,
        IProgress<BuildScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Fail("Choose the folder that holds your source repositories.");

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result.Fail(new ExceptionalError(ex.Message, ex));
        }
        if (!Directory.Exists(full))
            return Result.Fail($"Folder not found: {root}");

        var artifacts = new List<BuildArtifact>();
        var errors = new List<ScanError>();
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var projectFile in EnumerateProjects(full))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Several project files may share a folder (e.g. an old and a new one): measure it once.
                if (!projects.Add(Path.GetDirectoryName(projectFile)!))
                    continue;

                var projectDirectory = Path.GetDirectoryName(projectFile)!;
                foreach (var folderName in FolderNames)
                {
                    var folder = Path.Combine(projectDirectory, folderName);
                    if (!Directory.Exists(folder) || IsLink(folder))
                        continue;
                    var (files, bytes) = Measure(folder, cancellationToken);
                    artifacts.Add(new BuildArtifact(projectFile, folder, files, bytes));
                }

                progress?.Report(new BuildScanProgress(projects.Count, projectFile));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new ScanError(full, ex.Message));
        }

        artifacts.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return new BuildArtifactScan(full, artifacts, projects.Count, errors);
    }

    /// <summary>
    /// Permanently deletes a <c>bin</c> or <c>obj</c> folder found by <see cref="Scan"/>. Fails without
    /// touching anything when the folder is not a <c>bin</c>/<c>obj</c> folder directly next to an existing
    /// project file, or when it is a reparse point. Files and subfolders that cannot be deleted are counted
    /// in <see cref="BuildCleanResult.FailedEntries"/>; the rest of the folder is still removed.
    /// </summary>
    public static Result<BuildCleanResult> Clean(BuildArtifact artifact, CancellationToken cancellationToken = default) =>
        Validate(artifact).Map(folder => DeleteFolder(folder, cancellationToken));

    // The scan result is only a hint: re-check everything that makes a folder safe to delete. A project
    // may have moved or the folder may be a junction by now, so a stale row can never delete anything else.
    private static Result<string> Validate(BuildArtifact artifact)
    {
        string folder;
        string projectDirectory;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifact.Folder));
            projectDirectory = Path.GetDirectoryName(Path.GetFullPath(artifact.ProjectFile)) ?? string.Empty;
            if (projectDirectory.Length == 0)
                return Result.Fail<string>($"Project file not found: {artifact.ProjectFile}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result.Fail<string>(new ExceptionalError(ex.Message, ex));
        }

        if (!FolderNames.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            return Result.Fail<string>($"Not a bin or obj folder: {artifact.Folder}");
        if (!File.Exists(artifact.ProjectFile) || !IsProjectFile(artifact.ProjectFile))
            return Result.Fail<string>($"Project file not found: {artifact.ProjectFile}");
        if (!string.Equals(Path.GetDirectoryName(folder), projectDirectory, StringComparison.OrdinalIgnoreCase))
            return Result.Fail<string>($"'{artifact.Folder}' is not next to {Path.GetFileName(artifact.ProjectFile)}.");
        if (!Directory.Exists(folder))
            return Result.Fail<string>($"Folder no longer exists: {artifact.Folder}");
        if (IsLink(folder))
            return Result.Fail<string>($"Refusing to delete the link '{artifact.Folder}'; only real bin and obj folders are deleted.");
        return folder;
    }

    private static BuildCleanResult DeleteFolder(string folder, CancellationToken cancellationToken)
    {
        var stats = new DeleteStats();
        DeleteContents(folder, stats, cancellationToken);
        try
        {
            Directory.Delete(folder, recursive: false);
            stats.FoldersDeleted++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats.FailedEntries++; // something inside could not be deleted, so the folder is not empty
        }
        return new BuildCleanResult(stats.FoldersDeleted, stats.FilesDeleted, stats.FreedBytes, stats.FailedEntries);
    }

    // Depth-first, child folders before their parent. A reparse point is deleted as the link itself and
    // never entered, so the walk stays inside the bin/obj folder. Read-only files are unlocked first;
    // locked or protected entries are counted and skipped.
    private static void DeleteContents(string folder, DeleteStats stats, CancellationToken cancellationToken)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder)
                .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats.FailedEntries++;
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry is DirectoryInfo directory)
                {
                    if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        DeleteContents(directory.FullName, stats, cancellationToken);
                    directory.Delete();
                    stats.FoldersDeleted++;
                }
                else if (entry is FileInfo file)
                {
                    var length = file.Length;
                    if (file.IsReadOnly)
                        file.IsReadOnly = false;
                    file.Delete();
                    stats.FilesDeleted++;
                    stats.FreedBytes += length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stats.FailedEntries++;
            }
        }
    }

    private static IEnumerable<string> EnumerateProjects(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*proj", options))
        {
            if (IsProjectFile(path))
                yield return path;
        }
    }

    private static bool IsProjectFile(string path) =>
        ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable: treat it as unsafe to touch
        }
    }

    private static (long Files, long Bytes) Measure(string folder, CancellationToken cancellationToken)
    {
        long files = 0, bytes = 0;
        try
        {
            var entries = new FileSystemEnumerable<(bool IsDirectory, long Length)>(folder,
                (ref FileSystemEntry entry) => (entry.IsDirectory, entry.Length),
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
                });
            foreach (var (isDirectory, length) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isDirectory)
                {
                    files++;
                    bytes += length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Inaccessible files are not counted, as in FileScanner.
        }
        return (files, bytes);
    }

    private sealed class DeleteStats
    {
        public long FoldersDeleted;
        public long FilesDeleted;
        public long FreedBytes;
        public long FailedEntries;
    }
}
