using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>Options for creating a local user account.</summary>
public sealed record NewLocalUser(
    string Name,
    string Password,
    string FullName,
    string Description,
    bool PasswordNeverExpires,
    bool DisablePasswordChange,
    bool IsAdmin);

/// <summary>Result of creating a local user: success, or the exact Windows error text.</summary>
public sealed record CreateResult(bool Success, string? Error);

/// <summary>
/// Creates local user accounts via netapi32 (NetUserAdd level 2 + NetLocalGroupAddMember).
/// Requires Administrator privileges. Level 2 covers FullName too, in one call.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UserManagement
{
    private const int UfDontExpirePasswd = 0x10000;
    private const int UfPasswdCantChange = 0x0040;
    private const int ErrorAccessDenied = 5;
    private const int UfScript = 0x0001;
    private const int UfNormalAccount = 0x0200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo2
    {
        public string Name;
        public string Password;
        public int PasswordAge;
        public int Priv;
        public string HomeDir;
        public string Comment;
        public int Flags;
        public string ScriptPath;
        public int AuthFlags;
        public string FullName;
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
    private static extern int NetUserAdd(string? serverName, int level, ref UserInfo2 userInfo, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMember(string? serverName, string groupName, IntPtr memberSid);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? serverName, string user);

    public static CreateResult DeleteUser(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new CreateResult(false, "No user selected.");
            var rc = NetUserDel(null, name);
            return rc == 0 ? new CreateResult(true, null) : new CreateResult(false, Win32Message(rc));
        }
        catch (Exception ex)
        {
            return new CreateResult(false, ex.Message);
        }
    }

    public static CreateResult AddUser(NewLocalUser user)
    {
        try
        {
            return AddUserCore(user);
        }
        catch (Exception ex)
        {
            return new CreateResult(false, ex.Message);
        }
    }

    private static CreateResult AddUserCore(NewLocalUser user)
    {
        var error = Validate(user) ?? ValidateNotExists(user.Name);
        if (error is not null)
            return new CreateResult(false, error);

        // Priv=1 (USER_PRIV_USER) puts the account in "Users"; an account with no
        // group is unusable, so explicit group membership is added below.
        var info = new UserInfo2
        {
            Name = user.Name,
            Password = user.Password,
            PasswordAge = 0,
            Priv = 1,
            HomeDir = "",
            Comment = user.Description ?? "",
            Flags = UfScript | UfNormalAccount
                  | (user.PasswordNeverExpires ? UfDontExpirePasswd : 0)
                  | (user.DisablePasswordChange ? UfPasswdCantChange : 0),
            ScriptPath = "",
            FullName = user.FullName ?? "",
            UsrComment = "",
            Workstations = "",
            LogonServer = "",
            // 0 would mean "expired at 1970 / no storage"; -1 means never expires / unlimited.
            AcctExpires = -1,
            MaxStorage = -1,
        };

        var rc = NetUserAdd(null, 2, ref info, out _);
        if (rc != 0)
            return new CreateResult(false, Win32Message(rc));

        IntPtr sid;
        string groupName;
        try
        {
            groupName = BuiltinGroupName(user.IsAdmin
                ? System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid
                : System.Security.Principal.WellKnownSidType.BuiltinUsersSid);
            sid = GetAccountSid(user.Name);
        }
        catch (Exception ex)
        {
            // Roll back so a half-created account (no membership -> unusable) is not left behind.
            NetUserDel(null, user.Name);
            return new CreateResult(false, $"Account rolled back: could not resolve the group or account SID ({ex.Message}).");
        }
        try
        {
            rc = NetLocalGroupAddMember(null, groupName, sid);
        }
        finally
        {
            Marshal.FreeHGlobal(sid);
        }
        if (rc != 0)
        {
            // Roll back so a half-created account (no membership -> unusable) is not left behind.
            NetUserDel(null, user.Name);
            return new CreateResult(false, $"Account rolled back: adding it to '{groupName}' failed ({Win32Message(rc)}).");
        }

        return new CreateResult(true, null);
    }

    private static string? Validate(NewLocalUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Name))
            return "Enter a user name.";
        if (user.Name.Length > 20)
            return "The user name cannot be longer than 20 characters.";
        if (user.Name.Any(InvalidNameChar))
            return "The user name contains characters that are not allowed:  \" / \\ [ ] : ; | < > = , + * ? @";
        if (user.Password.Length < 8)
            return "The password must be at least 8 characters (Windows default policy).";
        return null;
    }

    private static bool InvalidNameChar(char c) =>
        "\"/\\[]:;|=,+*?<>@".Contains(c) || char.IsControl(c);

    private static string? ValidateNotExists(string name)
    {
        try
        {
            if (LocalUsers.GetAccounts().Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"A user named '{name}' already exists.";
        }
        catch (Exception)
        {
            // NetUserEnum failure must not block creation - NetUserAdd reports duplicates anyway.
        }
        return null;
    }

    /// <summary>Built-in group names are localized (e.g. "Yöneticiler"), so resolve them from the well-known SID.</summary>
    private static string BuiltinGroupName(System.Security.Principal.WellKnownSidType type)
    {
        var sid = new System.Security.Principal.SecurityIdentifier(type, null);
        var full = ((System.Security.Principal.NTAccount)sid.Translate(typeof(System.Security.Principal.NTAccount))).Value;
        return full[(full.IndexOf('\\') + 1)..];
    }

    /// <summary>Resolves the account SID. Caller frees the returned buffer with Marshal.FreeHGlobal.</summary>
    private static IntPtr GetAccountSid(string name)
    {
        // NTAccount.Translate wraps LookupAccountName correctly (in/out size params);
        // a hand-rolled P/Invoke of it is easy to get subtly wrong.
        var account = new System.Security.Principal.NTAccount(name);
        if (account.Translate(typeof(System.Security.Principal.SecurityIdentifier))
            is not System.Security.Principal.SecurityIdentifier sid)
            throw new Win32Exception(1332); // No mapping between account names and security IDs

        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        return buffer;
    }

    private static string Win32Message(int rc) => rc == ErrorAccessDenied
        ? "Access is denied. Restart the app as administrator and try again."
        : new Win32Exception(rc).Message + $" (0x{rc:X})";
}
