using System.Runtime.InteropServices;

namespace WindowsUtils.AI;

/// <summary>Minimal Windows Credential Manager wrapper (advapi32): secrets are stored
/// OS-encrypted per-user instead of cleartext. Windows-only at runtime.</summary>
public static class CredentialStore
{
    private const string Target = "WindowsUtils_AIChat_ApiKey";
    private const int Generic = 1;
    private const int LocalMachine = 2;

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

    public static void Save(string secret)
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
                TargetName = Target,
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

    public static string? Load()
    {
        if (!CredRead(Target, Generic, 0, out var ptr) || ptr == IntPtr.Zero)
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

    public static void Delete()
    {
        CredDelete(Target, Generic, 0);
    }
}
