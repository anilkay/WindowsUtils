using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentResults;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>Options for creating a local user account.</summary>
/// <remarks><see cref="ToString"/> hides the <see cref="Password"/>: record output easily ends up
/// in a log or exception message, and one stray log line would leak the secret in cleartext.</remarks>
public sealed record NewLocalUser(
    string Name,
    string Password,
    string FullName,
    string Description,
    bool PasswordNeverExpires,
    bool DisablePasswordChange,
    bool IsAdmin)
{
    public sealed override string ToString() =>
        $"{nameof(Name)}: {Name}, {nameof(FullName)}: {FullName}, {nameof(Description)}: {Description}, "
        + $"{nameof(PasswordNeverExpires)}: {PasswordNeverExpires}, {nameof(DisablePasswordChange)}: {DisablePasswordChange}, "
        + $"{nameof(IsAdmin)}: {IsAdmin}, {nameof(Password)}: ***";
}

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

    /// <summary>Deletes a local user. Fails with the exact Windows error text.</summary>
    public static Result DeleteUser(string name) => Guard(() =>
        Result.FailIf(string.IsNullOrWhiteSpace(name), "No user selected.")
            .Bind(() => NetResult(NetUserDel(null, name))));

    /// <summary>Creates a local user as one railway: validate → must not exist yet → NetUserAdd →
    /// add to Users/Administrators. Fails with the exact Windows error text; a failure after the
    /// account was created rolls it back.</summary>
    public static Result AddUser(NewLocalUser user) => Guard(() =>
        Validate(user)
            .Bind(() => ValidateNotExists(user.Name))
            .Bind(() => CreateAccount(user))
            .Bind(() => AddToBuiltinGroup(user)));

    // Unexpected exceptions (P/Invoke, SID translation, ...) end on the failure track too.
    private static Result Guard(Func<Result> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex.Message, ex));
        }
    }

    private static Result NetResult(int rc) => Result.OkIf(rc == 0, () => Win32Message(rc));

    private static Result CreateAccount(NewLocalUser user)
    {
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
        return NetResult(NetUserAdd(null, 2, ref info, out _));
    }

    // Runs after NetUserAdd: on failure the account is rolled back so a half-created
    // account (no membership -> unusable) is not left behind.
    private static Result AddToBuiltinGroup(NewLocalUser user)
    {
        var result = ResolveMembership(user)
            .Bind(m => AddMember(m.GroupName, m.Sid));
        if (result.IsFailed)
            NetUserDel(null, user.Name);
        return result;
    }

    private static Result<(string GroupName, byte[] Sid)> ResolveMembership(NewLocalUser user)
    {
        try
        {
            var groupName = BuiltinGroupName(user.IsAdmin
                ? System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid
                : System.Security.Principal.WellKnownSidType.BuiltinUsersSid);
            return (groupName, GetAccountSid(user.Name));
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(
                $"Account rolled back: could not resolve the group or account SID ({ex.Message}).", ex));
        }
    }

    private static Result AddMember(string groupName, byte[] sid)
    {
        var buffer = Marshal.AllocHGlobal(sid.Length);
        int rc;
        try
        {
            Marshal.Copy(sid, 0, buffer, sid.Length);
            rc = NetLocalGroupAddMember(null, groupName, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return Result.OkIf(rc == 0, () => $"Account rolled back: adding it to '{groupName}' failed ({Win32Message(rc)}).");
    }

    private static Result Validate(NewLocalUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Name))
            return Result.Fail("Enter a user name.");
        if (user.Name.Length > 20)
            return Result.Fail("The user name cannot be longer than 20 characters.");
        if (user.Name.Any(InvalidNameChar))
            return Result.Fail("The user name contains characters that are not allowed:  \" / \\ [ ] : ; | < > = , + * ? @");
        if (user.Password.Length < 8)
            return Result.Fail("The password must be at least 8 characters (Windows default policy).");
        return Result.Ok();
    }

    private static bool InvalidNameChar(char c) =>
        "\"/\\[]:;|=,+*?<>@".Contains(c) || char.IsControl(c);

    private static Result ValidateNotExists(string name)
    {
        try
        {
            if (LocalUsers.GetAccounts().Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return Result.Fail($"A user named '{name}' already exists.");
        }
        catch (Exception)
        {
            // NetUserEnum failure must not block creation - NetUserAdd reports duplicates anyway.
        }
        return Result.Ok();
    }

    /// <summary>Built-in group names are localized (e.g. "Yöneticiler"), so resolve them from the well-known SID.</summary>
    private static string BuiltinGroupName(System.Security.Principal.WellKnownSidType type)
    {
        var sid = new System.Security.Principal.SecurityIdentifier(type, null);
        var full = ((System.Security.Principal.NTAccount)sid.Translate(typeof(System.Security.Principal.NTAccount))).Value;
        return full[(full.IndexOf('\\') + 1)..];
    }

    /// <summary>Resolves the account SID in its binary form.</summary>
    private static byte[] GetAccountSid(string name)
    {
        // NTAccount.Translate wraps LookupAccountName correctly (in/out size params);
        // a hand-rolled P/Invoke of it is easy to get subtly wrong.
        var account = new System.Security.Principal.NTAccount(name);
        if (account.Translate(typeof(System.Security.Principal.SecurityIdentifier))
            is not System.Security.Principal.SecurityIdentifier sid)
            throw new Win32Exception(1332); // No mapping between account names and security IDs

        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static string Win32Message(int rc) => rc == ErrorAccessDenied
        ? "Access is denied. Restart the app as administrator and try again."
        : new Win32Exception(rc).Message + $" (0x{rc:X})";
}
