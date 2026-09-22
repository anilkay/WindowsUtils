using System.Buffers;
using System.Security.Cryptography;

namespace WindowsUtils.Core.Hashing;

/// <summary>
/// Computes file hashes in a single streaming pass over the file.
/// UI-agnostic: usable from WinForms, console apps, services, etc.
/// </summary>
public static class FileHasher
{
    public static IReadOnlyDictionary<HashAlgorithmName, string> ComputeHashes(
        string path,
        IEnumerable<HashAlgorithmName> algorithms,
        CancellationToken cancellationToken = default)
    {
        var selected = algorithms.Distinct().ToList();
        if (selected.Count == 0)
            throw new ArgumentException("Select at least one hash algorithm.", nameof(algorithms));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        var instances = selected.Select(a => (Algorithm: a, Instance: Create(a))).ToList();
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var (_, instance) in instances)
                        instance.TransformBlock(buffer, 0, read, null, 0);
                }
                foreach (var (_, instance) in instances)
                    instance.TransformFinalBlock(buffer, 0, 0);

                return instances.ToDictionary(
                    x => x.Algorithm,
                    x => Convert.ToHexString(x.Instance.Hash!).ToLowerInvariant());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            foreach (var (_, instance) in instances)
                instance.Dispose();
        }
    }

    public static Task<IReadOnlyDictionary<HashAlgorithmName, string>> ComputeHashesAsync(
        string path,
        IEnumerable<HashAlgorithmName> algorithms,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ComputeHashes(path, algorithms, cancellationToken), cancellationToken);

    /// <summary>Returns the algorithm whose computed hash equals <paramref name="candidate"/> (case-insensitive), or null.</summary>
    public static HashAlgorithmName? FindMatch(IReadOnlyDictionary<HashAlgorithmName, string> hashes, string candidate)
    {
        var normalized = candidate.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return null;

        foreach (var (algorithm, hash) in hashes)
        {
            if (hash == normalized)
                return algorithm;
        }
        return null;
    }

    private static HashAlgorithm Create(HashAlgorithmName name)
    {
        if (name == HashAlgorithmName.MD5) return MD5.Create();
        if (name == HashAlgorithmName.SHA1) return SHA1.Create();
        if (name == HashAlgorithmName.SHA256) return SHA256.Create();
        if (name == HashAlgorithmName.SHA384) return SHA384.Create();
        if (name == HashAlgorithmName.SHA512) return SHA512.Create();
        throw new NotSupportedException($"Unsupported hash algorithm: '{name.Name}'.");
    }
}
