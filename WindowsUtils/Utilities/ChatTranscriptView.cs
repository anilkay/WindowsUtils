using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsUtils.Utilities;

/// <summary>
/// Chat transcript for AI Chat: a read-only RichTextBox that shows each message under a
/// "You" / "Assistant" header and renders the assistant's Markdown (headings, bold, italic,
/// inline code, code blocks, lists, quotes, tables). Streaming text is re-rendered in place,
/// throttled, and the view only follows new text while it is scrolled to the bottom.
/// Keeps the raw messages so the chat can be exported as Markdown.
/// </summary>
public sealed partial class ChatTranscriptView : RichTextBox
{
    private const int BodyIndent = 28;
    private const int ListIndent = 22;

    private static readonly Font ItalicFont = new("Segoe UI", 10F, FontStyle.Italic);
    private static readonly Font BoldItalicFont = new("Segoe UI Semibold", 10F, FontStyle.Italic);
    private static readonly Font MonoBoldFont = new("Consolas", 10F, FontStyle.Bold);
    private static readonly Font SmallFont = new("Segoe UI", 8.5F);
    private static readonly Font GapFont = new("Segoe UI", 5F);
    private static readonly Font[] HeadingFonts =
    [
        new("Segoe UI Semibold", 15F),
        new("Segoe UI Semibold", 13F),
        new("Segoe UI Semibold", 11F),
    ];

    private readonly List<(string Role, StringBuilder Text)> _messages = [];
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 120 };
    private StringBuilder? _streaming;
    private int _streamStart;

    public ChatTranscriptView()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        WordWrap = true;
        DetectUrls = true;
        Font = Theme.BodyFont;
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RenderStreaming();
        };
        LinkClicked += (_, e) => OpenLink(e.LinkText ?? "");
    }

    /// <summary>True when there is at least one message (notes do not count).</summary>
    public bool HasMessages => _messages.Count > 0;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UseDarkScrollBars(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _renderTimer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Gives a control's scroll bars the dark Windows style in dark mode.</summary>
    internal static void UseDarkScrollBars(Control control)
    {
        if (Theme.IsDark && control.IsHandleCreated)
            _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    public void ClearTranscript()
    {
        _renderTimer.Stop();
        _streaming = null;
        _messages.Clear();
        Clear();
    }

    /// <summary>A gray informational line (welcome text, "cancelled", retries).</summary>
    public void AddNote(string text) => Batch(() =>
    {
        StartBlock();
        Paragraph(0);
        Write(text, ItalicFont, Theme.SubtleText);
        Write("\n", Theme.BodyFont, Theme.Text);
    });

    public void AddError(string text) => Batch(() =>
    {
        StartBlock();
        Paragraph(BodyIndent);
        Write(text, Theme.BodyFont, Theme.Danger);
        Write("\n", Theme.BodyFont, Theme.Text);
    });

    public void AddUserMessage(string text)
    {
        _messages.Add(("You", new StringBuilder(text)));
        Batch(() =>
        {
            WriteHeader("\uE13D", "You", Theme.Accent);
            // User text is shown as typed, not interpreted as Markdown.
            foreach (var line in SplitLines(text))
            {
                Paragraph(BodyIndent);
                Write(line + "\n", Theme.BodyFont, Theme.Text);
            }
        });
    }

    public void BeginAssistantMessage()
    {
        EndAssistantMessage();
        _streaming = new StringBuilder();
        _messages.Add(("Assistant", _streaming));
        Batch(() =>
        {
            WriteHeader("\uE99A", "Assistant", Theme.Text);
            _streamStart = TextLength;
        });
    }

    public void AppendAssistantText(string text)
    {
        if (_streaming is null)
            BeginAssistantMessage();
        _streaming!.Append(text);
        if (!_renderTimer.Enabled)
            _renderTimer.Start();
    }

    /// <summary>Renders the final text of the streaming message (no-op when none is streaming).</summary>
    public void EndAssistantMessage()
    {
        if (_streaming is null)
            return;
        _renderTimer.Stop();
        RenderStreaming();
        if (_streaming.Length == 0)
            _messages.RemoveAt(_messages.Count - 1);
        _streaming = null;
    }

    /// <summary>The conversation as Markdown, with the text as the model sent it.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var (role, text) in _messages)
            sb.Append("### ").Append(role).Append("\n\n").Append(text.ToString().Trim()).Append("\n\n");
        return sb.ToString();
    }

    private void RenderStreaming()
    {
        if (_streaming is null || IsDisposed)
            return;
        var markdown = _streaming.ToString();
        Batch(() =>
        {
            // The streaming message is always the last thing in the view: drop it and redraw.
            // SelectedText = "" is ignored by RichTextBox, so replace the range natively.
            Select(_streamStart, TextLength - _streamStart);
            SendMessage(Handle, EmReplaceSel, IntPtr.Zero, "");
            RenderMarkdown(markdown);
        });
    }

    // ---------- Markdown ----------

    private void RenderMarkdown(string markdown)
    {
        var lines = SplitLines(markdown);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                var language = fence.Groups[2].Value;
                var code = new List<string>();
                var marker = fence.Groups[1].Value;
                for (i++; i < lines.Count && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                WriteCodeBlock(language, code);
                continue;
            }

            if (IsTableRow(trimmed) && i + 1 < lines.Count && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
            {
                var rows = new List<string[]> { SplitTableRow(trimmed) };
                for (i += 2; i < lines.Count && IsTableRow(lines[i].TrimStart()); i++)
                    rows.Add(SplitTableRow(lines[i].TrimStart()));
                i--;
                WriteTable(rows);
                continue;
            }

            if (trimmed.Length == 0)
            {
                Gap();
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                var level = Math.Min(heading.Groups[1].Length, HeadingFonts.Length) - 1;
                Paragraph(BodyIndent);
                WriteInline(heading.Groups[2].Value, HeadingFonts[level], Theme.Text);
                Write("\n", Theme.BodyFont, Theme.Text);
                continue;
            }

            if (RuleRegex().IsMatch(trimmed))
            {
                Paragraph(BodyIndent);
                Write(new string('\u2500', 40) + "\n", Theme.BodyFont, Theme.Border);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                Paragraph(BodyIndent + 14);
                WriteInline(trimmed.TrimStart('>', ' '), ItalicFont, Theme.SubtleText);
                Write("\n", Theme.BodyFont, Theme.Text);
                continue;
            }

            var depth = (line.Length - trimmed.Length) / 2;
            var list = ListRegex().Match(trimmed);
            if (list.Success)
            {
                var ordered = char.IsDigit(list.Groups[1].Value[0]);
                var indent = BodyIndent + depth * ListIndent;
                Paragraph(indent + ListIndent, -ListIndent);
                Write(ordered ? list.Groups[1].Value + "\t" : "\u2022\t", ordered ? Theme.BodyFont : Theme.SemiboldFont, Theme.SubtleText);
                WriteInline(trimmed[list.Length..], Theme.BodyFont, Theme.Text);
                Write("\n", Theme.BodyFont, Theme.Text);
                continue;
            }

            Paragraph(BodyIndent);
            WriteInline(trimmed, Theme.BodyFont, Theme.Text);
            Write("\n", Theme.BodyFont, Theme.Text);
        }
    }

    /// <summary>Inline Markdown: `code`, **bold**, *italic*, ***both***, [text](url).</summary>
    private void WriteInline(string text, Font font, Color color)
    {
        var bold = font.Bold || font.Name.Contains("Semibold", StringComparison.Ordinal);
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
                Write(plain.ToString(), font, color);
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    Write("\u00A0" + text[(i + 1)..end] + "\u00A0", Theme.MonoFont, color, Theme.Background);
                    i = end;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                var width = text.AsSpan(i).StartsWith("***") ? 3 : text.AsSpan(i).StartsWith("**") ? 2 : 1;
                var end = text.IndexOf(new string('*', width), i + width, StringComparison.Ordinal);
                if (end > i + width)
                {
                    Flush();
                    var inner = text[(i + width)..end];
                    var styled = width switch
                    {
                        3 => BoldItalicFont,
                        2 => bold ? font : Theme.SemiboldFont,
                        _ => bold ? BoldItalicFont : ItalicFont,
                    };
                    Write(inner, font.Size > Theme.BodyFont.Size && width == 2 ? font : styled, color);
                    i = end + width - 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var link = LinkRegex().Match(text, i);
                if (link.Success && link.Index == i)
                {
                    Flush();
                    Write(link.Groups[1].Value, font, Theme.Accent);
                    // Keep the address visible so DetectUrls makes it clickable.
                    Write($" ({link.Groups[2].Value})", SmallFont, Theme.SubtleText);
                    i += link.Length - 1;
                    continue;
                }
            }
            plain.Append(c);
        }
        Flush();
    }

    private void WriteCodeBlock(string language, List<string> code)
    {
        if (language.Length > 0)
        {
            Paragraph(BodyIndent + 4);
            Write(language + "\n", SmallFont, Theme.SubtleText);
        }
        // Pad lines to one width so the highlighted background reads as a block.
        var width = Math.Min(code.Count == 0 ? 0 : code.Max(l => l.Length), 100);
        foreach (var line in code.Count == 0 ? [""] : code)
        {
            Paragraph(BodyIndent);
            Write(" " + line.Replace("\t", "    ").PadRight(width) + " ", Theme.MonoFont, Theme.Text, Theme.Background);
            Write("\n", Theme.MonoFont, Theme.Text);
        }
        Gap();
    }

    private void WriteTable(List<string[]> rows)
    {
        var columns = rows.Max(r => r.Length);
        var widths = new int[columns];
        foreach (var row in rows)
            for (var c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], StripInline(row[c]).Length);

        for (var r = 0; r < rows.Count; r++)
        {
            var sb = new StringBuilder(" ");
            for (var c = 0; c < columns; c++)
            {
                var cell = c < rows[r].Length ? StripInline(rows[r][c]) : "";
                sb.Append(cell.PadRight(widths[c])).Append(c < columns - 1 ? "   " : " ");
            }
            Paragraph(BodyIndent);
            Write(sb.ToString(), r == 0 ? MonoBoldFont : Theme.MonoFont, Theme.Text, r == 0 ? Theme.Selection : Theme.Background);
            Write("\n", Theme.MonoFont, Theme.Text);
        }
        Gap();
    }

    private static bool IsTableRow(string line) => line.StartsWith('|') && line.Length > 1;

    private static string[] SplitTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static string StripInline(string text) => text.Replace("**", "").Replace("`", "");

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')];

    [GeneratedRegex(@"^\s*(```+|~~~+)\s*([\w#+.-]*)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^([-*+]|\d{1,3}[.)])\s+")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    // ---------- Low-level writing ----------

    private void WriteHeader(string icon, string name, Color color)
    {
        StartBlock();
        Paragraph(0);
        Write(icon, Theme.IconFont, color);
        Write("  " + name + "\n", Theme.SemiboldFont, color);
    }

    /// <summary>Leaves a small gap before a new message or note (not at the very top).</summary>
    private void StartBlock()
    {
        if (TextLength > 0)
        {
            Gap();
            Gap();
        }
    }

    private void Gap()
    {
        Paragraph(0);
        Write("\n", GapFont, Theme.Text);
    }

    private void Paragraph(int indent, int firstLineOffset = 0)
    {
        Select(TextLength, 0);
        // SelectionIndent is the first line; SelectionHangingIndent is relative to it.
        SelectionIndent = indent + firstLineOffset;
        SelectionHangingIndent = -firstLineOffset;
        SelectionRightIndent = 8;
        if (firstLineOffset != 0)
            SelectionTabs = [-firstLineOffset];
        else
            SelectionTabs = [];
    }

    private void Write(string text, Font font, Color color, Color? background = null)
    {
        Select(TextLength, 0);
        SelectionFont = font;
        SelectionColor = color;
        SelectionBackColor = background ?? BackColor;
        SelectedText = text;
    }

    /// <summary>Runs a change without flicker, keeping the scroll position and the
    /// user's selection; follows the new text only if the view was at the bottom.</summary>
    private void Batch(Action change)
    {
        if (!IsHandleCreated)
        {
            change();
            return;
        }

        var atBottom = IsScrolledToBottom();
        var scroll = new Point();
        SendMessage(Handle, EmGetScrollPos, IntPtr.Zero, ref scroll);
        var selectionStart = SelectionStart;
        var selectionLength = SelectionLength;

        SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            change();
            if (selectionStart + selectionLength <= TextLength && selectionLength > 0)
                Select(selectionStart, selectionLength);
            else
                Select(TextLength, 0);
        }
        finally
        {
            if (atBottom)
            {
                Select(TextLength, 0);
                ScrollToCaret();
            }
            else
            {
                SendMessage(Handle, EmSetScrollPos, IntPtr.Zero, ref scroll);
            }
            SendMessage(Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
            Invalidate();
        }
    }

    private bool IsScrolledToBottom()
    {
        var info = new ScrollInfo { cbSize = (uint)Marshal.SizeOf<ScrollInfo>(), fMask = SifAll };
        if (!GetScrollInfo(Handle, SbVert, ref info) || info.nPage == 0)
            return true;
        return info.nPos + (int)info.nPage >= info.nMax - 4;
    }

    private static void OpenLink(string link)
    {
        // Only web links: file/UNC links from model output could reach arbitrary hosts.
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // No browser registered: ignore.
            }
        }
    }

    private const int WmSetRedraw = 0x000B;
    private const int EmReplaceSel = 0x00C2;
    private const int EmGetScrollPos = 0x04DD;
    private const int EmSetScrollPos = 0x04DE;
    private const int SbVert = 1;
    private const uint SifAll = 0x17;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);
}
