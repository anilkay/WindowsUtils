using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using FluentResults;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>Errors or warnings from one source in one log, folded into a single row.</summary>
public sealed record EventSourceSummary(
    string Log,
    string Source,
    string Level,
    int Count,
    DateTime Latest,
    string EventIds,
    string LatestMessage);

/// <summary>
/// Summarises recent critical/error/warning events in the System and Application logs by source,
/// read through the Windows Event Log API (wevtapi: EvtQuery/EvtNext/EvtRender), so Core needs no
/// System.Diagnostics.EventLog package. Read-only. Only the newest event of each group has its
/// message formatted (EvtFormatMessage is the slow part). Handles are IntPtr and every struct is
/// avoided (events are rendered as XML), so the calls are the same on x64 and arm64.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EventLogSummary
{
    public static readonly string[] Logs = ["System", "Application"];

    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryReverseDirection = 0x200;
    private const int EvtRenderEventXml = 1;
    private const int EvtFormatMessageEvent = 1;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorEvtChannelNotFound = 15007;
    private const int MaxEventsPerLog = 5000;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Groups of events from the last <paramref name="hours"/> hours, most frequent first.</summary>
    /// <param name="includeWarnings">Also include Warning (level 3), not only Critical and Error.</param>
    public static Result<IReadOnlyList<EventSourceSummary>> Get(int hours, bool includeWarnings, CancellationToken cancellationToken = default)
    {
        var results = new List<EventSourceSummary>();
        foreach (var log in Logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = Summarise(log, Math.Clamp(hours, 1, 24 * 30), includeWarnings, cancellationToken);
            if (summary.IsFailed)
                return summary.ToResult<IReadOnlyList<EventSourceSummary>>();
            results.AddRange(summary.Value);
        }
        return results
            .OrderByDescending(s => LevelRank(s.Level))
            .ThenByDescending(s => s.Count)
            .ThenByDescending(s => s.Latest)
            .ToList();
    }

    public static string LevelName(int level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        _ => $"Level {level}",
    };

    private static int LevelRank(string level) => level switch
    {
        "Critical" => 3,
        "Error" => 2,
        "Warning" => 1,
        _ => 0,
    };

    private sealed class Group(string source, int level, DateTime latest, IntPtr latestEvent)
    {
        public string Source { get; } = source;
        public int Level { get; } = level;
        public DateTime Latest { get; } = latest;
        public IntPtr LatestEvent { get; } = latestEvent;
        public int Count { get; set; } = 1;
        public SortedSet<int> EventIds { get; } = [];
    }

    private static Result<List<EventSourceSummary>> Summarise(string log, int hours, bool includeWarnings, CancellationToken cancellationToken)
    {
        var levels = includeWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";
        var xpath = $"*[System[({levels}) and TimeCreated[timediff(@SystemTime) <= {(long)hours * 3600 * 1000}]]]";
        var query = EvtQuery(IntPtr.Zero, log, xpath, EvtQueryChannelPath | EvtQueryReverseDirection);
        if (query == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorEvtChannelNotFound
                ? new List<EventSourceSummary>()
                : Result.Fail(new ExceptionalError($"Could not open the {log} event log: {new Win32Exception(error).Message}",
                    new Win32Exception(error)));
        }

        var groups = new Dictionary<(string, int), Group>();
        var publishers = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        var handles = new IntPtr[64];
        try
        {
            var read = 0;
            while (read < MaxEventsPerLog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!EvtNext(query, handles.Length, handles, 0, 0, out var returned))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        break;
                    return Result.Fail(new ExceptionalError($"Could not read the {log} event log: {new Win32Exception(error).Message}",
                        new Win32Exception(error)));
                }

                for (var i = 0; i < returned; i++)
                {
                    read++;
                    var handle = handles[i];
                    var keep = false;
                    try
                    {
                        if (RenderXml(handle) is not { } xml || Parse(xml) is not { } e)
                            continue;
                        if (groups.TryGetValue((e.Source, e.Level), out var group))
                        {
                            group.Count++;
                            group.EventIds.Add(e.EventId);
                        }
                        else
                        {
                            // Reverse direction: the first event seen for a group is its newest one.
                            group = new Group(e.Source, e.Level, e.Time, handle);
                            group.EventIds.Add(e.EventId);
                            groups[(e.Source, e.Level)] = group;
                            keep = true;
                        }
                    }
                    finally
                    {
                        if (!keep)
                            EvtClose(handle);
                    }
                }
            }

            return groups.Values.Select(g => new EventSourceSummary(
                log,
                g.Source,
                LevelName(g.Level),
                g.Count,
                g.Latest,
                FormatIds(g.EventIds),
                FormatMessage(g.Source, g.LatestEvent, publishers))).ToList();
        }
        finally
        {
            foreach (var group in groups.Values)
                EvtClose(group.LatestEvent);
            foreach (var publisher in publishers.Values)
                if (publisher != IntPtr.Zero)
                    EvtClose(publisher);
            EvtClose(query);
        }
    }

    private static string FormatIds(SortedSet<int> ids) =>
        ids.Count <= 6 ? string.Join(", ", ids) : string.Join(", ", ids.Take(6)) + $" (+{ids.Count - 6})";

    private readonly record struct ParsedEvent(string Source, int Level, int EventId, DateTime Time);

    private static ParsedEvent? Parse(string xml)
    {
        try
        {
            var system = XElement.Parse(xml).Element(Ns + "System");
            if (system is null)
                return null;
            var source = (string?)system.Element(Ns + "Provider")?.Attribute("Name") ?? "(unknown)";
            var level = int.TryParse((string?)system.Element(Ns + "Level"), out var l) ? l : 0;
            var id = int.TryParse((string?)system.Element(Ns + "EventID"), out var i) ? i : 0;
            var time = DateTime.TryParse((string?)system.Element(Ns + "TimeCreated")?.Attribute("SystemTime"),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToLocalTime()
                : DateTime.MinValue;
            return new ParsedEvent(source, level, id, time);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? RenderXml(IntPtr handle)
    {
        var size = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (EvtRender(IntPtr.Zero, handle, EvtRenderEventXml, size, buffer, out var used, out _))
                    return Marshal.PtrToStringUni(buffer);
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return null;
                size = used;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    // The event's message text in the user's language, from the source's message table.
    // Best effort: sources without installed message files (or uninstalled ones) give "".
    private static string FormatMessage(string source, IntPtr handle, Dictionary<string, IntPtr> publishers)
    {
        if (!publishers.TryGetValue(source, out var metadata))
        {
            metadata = EvtOpenPublisherMetadata(IntPtr.Zero, source, null, 0, 0);
            publishers[source] = metadata;
        }
        if (metadata == IntPtr.Zero)
            return "";

        var size = 2048;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size * 2);
            try
            {
                if (EvtFormatMessage(metadata, handle, 0, 0, IntPtr.Zero, EvtFormatMessageEvent, size, buffer, out var used))
                {
                    var text = Marshal.PtrToStringUni(buffer) ?? "";
                    text = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
                    return text.Length <= 500 ? text : text[..500] + "...";
                }
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return "";
                size = used;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return "";
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(IntPtr resultSet, int eventsSize, [Out] IntPtr[] events, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, IntPtr buffer,
        out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtOpenPublisherMetadata(IntPtr session, string publisherId, string? logFilePath, int locale, int flags);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtFormatMessage(IntPtr publisherMetadata, IntPtr eventHandle, int messageId, int valueCount,
        IntPtr values, int flags, int bufferSize, IntPtr buffer, out int bufferUsed);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);
}
