using System.Runtime.InteropServices;

namespace WindowsUtils.AI;

/// <summary>Minimal Windows Credential Manager wrapper (advapi32): secrets are stored
/// OS-encrypted per-user instead of cleartext. Windows-only at runtime.</summary>
public static class CredentialStore
{
    // The target name embeds the endpoint's host: one endpoint's key must never be loaded for
    // (and thus sent to) a different endpoint. Hostless storage was the old, risky shape.
    private const string TargetPrefix = "WindowsUtils_AIChat_ApiKey";
    private const int Generic = 1;
    private const int LocalMachine = 2;

    private static string Target(string? host) =>
        string.IsNullOrWhiteSpace(host) ? TargetPrefix : $"{TargetPrefix}:{host.Trim().ToLowerInvariant()}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    /// <summary>Saves the key for an endpoint's host. Throws when the host is unknown: an
    /// unbound key could later be sent to whatever endpoint happens to be typed next.</summary>
    public static void Save(string secret, string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("An API key can only be saved together with the endpoint host it belongs to.", nameof(host));
        Write(Target(host), secret);
    }

    /// <summary>Reads the key saved for this endpoint's host — never another host's. A stored-but
    /// never-host-bound key (old shape) is migrated to this host on the first read.</summary>
    public static string? Load(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        var value = Read(Target(host));
        if (value is not null)
            return value;
        value = Read(TargetPrefix);
        if (value is null)
            return null;
        try
        {
            Write(Target(host), value);
            DeleteTarget(TargetPrefix);
        }
        catch
        {
            // Best effort: if the migration fails, the legacy copy stays and is still returned.
        }
        return value;
    }

    /// <summary>Deletes the key saved for this endpoint's host; an unknown host clears the legacy copy.</summary>
    public static void Delete(string host) => DeleteTarget(Target(host));

    private static void Write(string target, string secret)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Flags = 0,
                Type = Generic,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachine,
            };
            if (!CredWrite(ref credential, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
            Array.Clear(bytes);
        }
    }

    private static string? Read(string target)
    {
        if (!CredRead(target, Generic, 0, out var ptr) || ptr == IntPtr.Zero)
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(ptr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var secret = System.Text.Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return secret.Length > 0 ? secret : null;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    private static void DeleteTarget(string target) => CredDelete(target, Generic, 0);
}
