using System.Runtime.InteropServices;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>A local user account on this PC.</summary>
public sealed record LocalUserAccount(
    string Name,
    string FullName,
    string Description,
    bool Enabled,
    bool LockedOut,
    bool IsAdmin,
    bool IsLimited,
    bool PasswordNeverExpires,
    DateTime? LastLogon,
    DateTime? PasswordLastSet,
    int LogonCount);

/// <summary>Read-only view of the local user accounts (netapi32 NetUserEnum, level 2).</summary>
public static class LocalUsers
{
    private const int FilterNormalAccount = 0x0002;
    private const int MaxPreferredLength = -1;
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;

    private const int UserPrivGuest = 0;
    private const int UserPrivAdmin = 2;

    private const int UfAccountDisable = 0x0002;
    private const int UfLockout = 0x0010;
    private const int UfDontExpirePasswd = 0x10000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo2
    {
        public string? Name;
        public IntPtr Password;
        public int PasswordAge;
        public int Priv;
        public string? HomeDir;
        public string? Comment;
        public int Flags;
        public string? ScriptPath;
        public int AuthFlags;
        public string? FullName;
        public string? UsrComment;
        public string? Parms;
        public string? Workstations;
        public int LastLogon;
        public int LastLogoff;
        public int AcctExpires;
        public int MaxStorage;
        public int UnitsPerWeek;
        public IntPtr LogonHours;
        public int BadPwCount;
        public int NumLogons;
        public string? LogonServer;
        public int CountryCode;
        public int CodePage;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? serverName, int level, int filter, out IntPtr buffer,
        int prefMaxLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Returns the local accounts sorted by name; throws on API failure.</summary>
    public static IReadOnlyList<LocalUserAccount> GetAccounts()
    {
        var accounts = new List<LocalUserAccount>();
        if (!OperatingSystem.IsWindows())
            return accounts;

        var now = DateTime.Now;
        int resume = 0;
        int status;
        do
        {
            status = NetUserEnum(null, 2, FilterNormalAccount, out var buffer, MaxPreferredLength,
                out var read, out _, ref resume);
            try
            {
                if (status != NerrSuccess && status != ErrorMoreData)
                    throw new System.ComponentModel.Win32Exception(status);

                var size = Marshal.SizeOf<UserInfo2>();
                for (int i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<UserInfo2>(buffer + i * size);
                    accounts.Add(new LocalUserAccount(
                        info.Name ?? "",
                        info.FullName ?? "",
                        info.Comment ?? "",
                        Enabled: (info.Flags & UfAccountDisable) == 0,
                        LockedOut: (info.Flags & UfLockout) != 0,
                        IsAdmin: info.Priv == UserPrivAdmin,
                        IsLimited: info.Priv == UserPrivGuest,
                        PasswordNeverExpires: (info.Flags & UfDontExpirePasswd) != 0,
                        // 0 means never logged on (or not recorded).
                        LastLogon: info.LastLogon == 0 ? null : DateTimeOffset.FromUnixTimeSeconds((uint)info.LastLogon).LocalDateTime,
                        PasswordLastSet: info.PasswordAge <= 0 ? null : now.AddSeconds(-(uint)info.PasswordAge),
                        LogonCount: info.NumLogons));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    NetApiBufferFree(buffer);
            }
        } while (status == ErrorMoreData);

        accounts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return accounts;
    }

    /// <summary>"Enabled", "Disabled" or "Locked out".</summary>
    public static string FormatStatus(LocalUserAccount a) =>
        a.LockedOut ? "Locked out" : a.Enabled ? "Enabled" : "Disabled";

    /// <summary>"Administrator", "Standard user" or "Limited" (guest privilege: Guest and built-in service accounts).</summary>
    public static string FormatType(LocalUserAccount a) =>
        a.IsAdmin ? "Administrator" : a.IsLimited ? "Limited" : "Standard user";
}
