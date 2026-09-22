namespace WindowsUtils.Core.IO;

/// <summary>A file found by <see cref="FileScanner.FindLargestFilesAsync"/>.</summary>
public sealed record LargestFileEntry(string FullPath, long Size, DateTime Modified);

/// <summary>Progress reported while scanning.</summary>
public sealed record ScanProgress(long Scanned, string? CurrentDirectory);

/// <summary>
/// Safe, cancellable file-system scanning. Skips inaccessible paths and reparse points
/// (junctions, symlinks, OneDrive placeholders), so scans never crash or loop.
/// </summary>
public static class FileScanner
{
    private static EnumerationOptions CreateOptions(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>Finds the <paramref name="maxResults"/> largest files under <paramref name="roots"/>, sorted largest first.</summary>
    public static (IReadOnlyList<LargestFileEntry> Files, long Scanned) FindLargestFiles(
        IEnumerable<string> roots,
        int maxResults,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool recursive = true) =>
        FindLargest(roots, maxResults, progress, cancellationToken, recursive);

    /// <summary>Finds the <paramref name="maxResults"/> largest files under <paramref name="roots"/>, sorted largest first.</summary>
    public static Task<(IReadOnlyList<LargestFileEntry> Files, long Scanned)> FindLargestFilesAsync(
        IEnumerable<string> roots,
        int maxResults,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool recursive = true) =>
        Task.Run(() => FindLargest(roots, maxResults, progress, cancellationToken, recursive), cancellationToken);

    private static (IReadOnlyList<LargestFileEntry> Files, long Scanned) FindLargest(
        IEnumerable<string> roots,
        int maxResults,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool recursive)
    {
        // Min-heap by size: keeps only the N largest files seen so far (constant memory).
        var heap = new PriorityQueue<LargestFileEntry, long>();

        long scanned = 0;
        var lastReport = Environment.TickCount64;

        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", CreateOptions(recursive)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                long size;
                DateTime modified;
                try
                {
                    var info = new FileInfo(path);
                    size = info.Length;
                    modified = info.LastWriteTime;
                }
                catch
                {
                    continue;
                }

                scanned++;

                if (heap.Count < maxResults)
                {
                    heap.Enqueue(new LargestFileEntry(path, size, modified), size);
                }
                else if (size > heap.Peek().Size)
                {
                    heap.Dequeue();
                    heap.Enqueue(new LargestFileEntry(path, size, modified), size);
                }

                var now = Environment.TickCount64;
                if (progress is not null && now - lastReport > 250)
                {
                    progress.Report(new ScanProgress(scanned, Path.GetDirectoryName(path)));
                    lastReport = now;
                }
            }
        }

        var result = new List<LargestFileEntry>(heap.Count);
        while (heap.Count > 0)
            result.Add(heap.Dequeue());
        result.Reverse(); // min-heap yields ascending -> flip to descending
        return (result, scanned);
    }

    /// <summary>Total size of all files under <paramref name="directory"/>, recursively. Inaccessible files are skipped.</summary>
    public static long GetDirectorySize(string directory, CancellationToken cancellationToken = default)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", CreateOptions(recursive: true)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { total += new FileInfo(file).Length; }
                catch { /* inaccessible file */ }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return total;
    }
}
