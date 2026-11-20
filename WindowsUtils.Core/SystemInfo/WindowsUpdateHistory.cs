using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FluentResults;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>One entry of the Windows Update history (installs and uninstalls).</summary>
public sealed record UpdateHistoryEntry(
    DateTime Date,
    string Title,
    string KbArticle,
    string Operation,
    string ResultText,
    bool Succeeded,
    string ErrorCode,
    string Client);

/// <summary>
/// The Windows Update history as Settings shows it, read through the Windows Update Agent COM API
/// (Microsoft.Update.Session → IUpdateSearcher.QueryHistory). Read-only; slow-ish, call off the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsUpdateHistory
{
    /// <summary>The newest <paramref name="max"/> history entries, newest first.</summary>
    public static Result<IReadOnlyList<UpdateHistoryEntry>> Get(int max = 200)
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is null)
            return Result.Fail("The Windows Update Agent (Microsoft.Update.Session) is not available on this PC.");

        object? session = null, searcher = null, history = null;
        try
        {
            session = Activator.CreateInstance(type);
            dynamic s = session!;
            searcher = s.CreateUpdateSearcher();
            dynamic searcherObj = searcher!;
            int total = searcherObj.GetTotalHistoryCount();
            if (total <= 0)
                return Result.Ok<IReadOnlyList<UpdateHistoryEntry>>([]);

            history = searcherObj.QueryHistory(0, Math.Min(total, Math.Clamp(max, 1, 1000)));
            var list = new List<UpdateHistoryEntry>();
            foreach (dynamic entry in (System.Collections.IEnumerable)history!)
            {
                try
                {
                    // Entries for driver/defender scans without a title are noise.
                    string title = entry.Title ?? "";
                    if (title.Length == 0)
                        continue;
                    int operation = entry.Operation;
                    int resultCode = entry.ResultCode;
                    int hresult = entry.HResult;
                    DateTime date = entry.Date;
                    string client = entry.ClientApplicationID ?? "";
                    list.Add(new UpdateHistoryEntry(
                        // The agent reports UTC, but COM hands it over with Kind = Unspecified.
                        DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime(),
                        title,
                        KbArticle(title),
                        OperationName(operation),
                        ResultName(resultCode),
                        resultCode is 2 or 3,
                        hresult == 0 ? "" : $"0x{hresult:X8}",
                        client));
                }
                finally
                {
                    Marshal.ReleaseComObject(entry);
                }
            }
            return list.OrderByDescending(e => e.Date).ToList();
        }
        catch (COMException ex)
        {
            return Result.Fail(new ExceptionalError(
                $"Could not read the Windows Update history (0x{ex.HResult:X8}). Is the Windows Update service disabled?", ex));
        }
        finally
        {
            foreach (var o in new[] { history, searcher, session })
                if (o is not null && Marshal.IsComObject(o))
                    Marshal.ReleaseComObject(o);
        }
    }

    // UpdateOperation enum.
    private static string OperationName(int operation) => operation switch
    {
        1 => "Install",
        2 => "Uninstall",
        _ => "Other",
    };

    // OperationResultCode enum.
    private static string ResultName(int code) => code switch
    {
        0 => "Not started",
        1 => "In progress",
        2 => "Succeeded",
        3 => "Succeeded with errors",
        4 => "Failed",
        5 => "Aborted",
        _ => $"Result {code}",
    };

    private static string KbArticle(string title) =>
        KbPattern().Match(title) is { Success: true } match ? match.Value.ToUpperInvariant() : "";

    [GeneratedRegex(@"KB\d{6,8}", RegexOptions.IgnoreCase)]
    private static partial Regex KbPattern();
}
