using System.Buffers;
using System.Security.Cryptography;
using FluentResults;

namespace WindowsUtils.Core.Hashing;

/// <summary>
/// Computes file hashes in a single streaming pass over the file.
/// UI-agnostic: usable from WinForms, console apps, services, etc.
/// </summary>
public static class FileHasher
{
    /// <summary>Hashes the file with every selected algorithm. Fails (instead of throwing) when no or an
    /// unsupported algorithm is selected, or the file cannot be read; the read error is kept as an
    /// <see cref="ExceptionalError"/>. Cancellation still throws <see cref="OperationCanceledException"/>.</summary>
    public static Result<IReadOnlyDictionary<HashAlgorithmName, string>> ComputeHashes(
        string path,
        IEnumerable<HashAlgorithmName> algorithms,
        CancellationToken cancellationToken = default)
    {
        var selected = algorithms.Distinct().ToList();
        return Result.FailIf(selected.Count == 0, "Select at least one hash algorithm.")
            .Bind(() => Result.Merge([.. selected.Select(RequireSupported)]))
            .Bind(() => HashFile(path, selected, cancellationToken));
    }

    public static Task<Result<IReadOnlyDictionary<HashAlgorithmName, string>>> ComputeHashesAsync(
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

    private static Result<IReadOnlyDictionary<HashAlgorithmName, string>> HashFile(
        string path, List<HashAlgorithmName> selected, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            var instances = selected.Select(a => (Algorithm: a, Instance: IncrementalHash.CreateHash(a))).ToList();
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var (_, instance) in instances)
                        instance.AppendData(buffer, 0, read);
                }

                return Result.Ok<IReadOnlyDictionary<HashAlgorithmName, string>>(instances.ToDictionary(
                    x => x.Algorithm,
                    x => Convert.ToHexStringLower(x.Instance.GetHashAndReset())));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                foreach (var (_, instance) in instances)
                    instance.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(new ExceptionalError(ex.Message, ex));
        }
    }

    private static Result RequireSupported(HashAlgorithmName name) =>
        Result.OkIf(
            name == HashAlgorithmName.MD5 ||
            name == HashAlgorithmName.SHA1 ||
            name == HashAlgorithmName.SHA256 ||
            name == HashAlgorithmName.SHA384 ||
            name == HashAlgorithmName.SHA512,
            $"Unsupported hash algorithm: '{name.Name}'.");
}
