using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsUtils;

/// <summary>Windows 11 (Fluent) style colors, fonts and control styling.
/// Follows the Windows light/dark setting and the Windows accent color.</summary>
public static class Theme
{
    private static readonly ConditionalWeakTable<Button, object> AccentButtons = new();
    private static readonly ConditionalWeakTable<Button, object> StyledButtons = new();

    public static bool IsDark { get; private set; }

    public static Color Background { get; private set; }
    public static Color Surface { get; private set; }
    public static Color Border { get; private set; }
    public static Color Text { get; private set; }
    public static Color SubtleText { get; private set; }
    public static Color DisabledText { get; private set; }
    public static Color NavHover { get; private set; }
    public static Color NavSelected { get; private set; }
    public static Color ControlFill { get; private set; }
    public static Color ControlHover { get; private set; }
    public static Color ControlPressed { get; private set; }
    public static Color ControlBorder { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }
    public static Color AccentDisabled { get; private set; }
    public static Color TextOnAccent { get; private set; }
    public static Color Selection { get; private set; }
    public static Color Success { get; private set; }
    public static Color Danger { get; private set; }
    public static Color DangerBackground { get; private set; }

    public static Font BodyFont { get; } = new("Segoe UI", 10F);
    public static Font SemiboldFont { get; } = new("Segoe UI Semibold", 10F);
    public static Font AppTitleFont { get; } = new("Segoe UI Semibold", 11F);
    public static Font TitleFont { get; } = new("Segoe UI Semibold", 20F);
    public static Font MonoFont { get; } = new("Consolas", 10F);
    public static Font IconFont { get; } = new(
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 12F);

    /// <summary>Reads the color mode chosen via <see cref="Application.SetColorMode"/> and the accent color.</summary>
    public static void Initialize()
    {
        IsDark = Application.IsDarkModeEnabled;
        if (IsDark)
        {
            Background = Hex(0x202020);
            Surface = Hex(0x2B2B2B);
            Border = Hex(0x3A3A3A);
            Text = Hex(0xFFFFFF);
            SubtleText = Hex(0xC5C5C5);
            DisabledText = Hex(0x787878);
            NavHover = Hex(0x2D2D2D);
            NavSelected = Hex(0x333333);
            ControlFill = Hex(0x2D2D2D);
            ControlHover = Hex(0x323232);
            ControlPressed = Hex(0x272727);
            ControlBorder = Hex(0x3F3F3F);
            AccentDisabled = Hex(0x434343);
            Success = Hex(0x6CCB5F);
            Danger = Hex(0xFF99A4);
            DangerBackground = Hex(0x442726);
        }
        else
        {
            Background = Hex(0xF3F3F3);
            Surface = Hex(0xFFFFFF);
            Border = Hex(0xE5E5E5);
            Text = Hex(0x1B1B1B);
            SubtleText = Hex(0x5F5F5F);
            DisabledText = Hex(0xA0A0A0);
            NavHover = Hex(0xEAEAEA);
            NavSelected = Hex(0xE4E4E4);
            ControlFill = Hex(0xFBFBFB);
            ControlHover = Hex(0xF6F6F6);
            ControlPressed = Hex(0xF0F0F0);
            ControlBorder = Hex(0xE0E0E0);
            AccentDisabled = Hex(0xC5C5C5);
            Success = Hex(0x0F7B0F);
            Danger = Hex(0xC42B1C);
            DangerBackground = Hex(0xFDE7E9);
        }

        Accent = ReadAccentColor(IsDark);
        AccentHover = Blend(Accent, Background, 0.1);
        AccentPressed = Blend(Accent, Background, 0.2);
        TextOnAccent = Accent.GetBrightness() > 0.55f ? Color.Black : Color.White;
        Selection = Blend(Surface, Accent, IsDark ? 0.3 : 0.18);
    }

    /// <summary>Styles all buttons and rich text boxes under <paramref name="root"/>.</summary>
    public static void Apply(Control root)
    {
        switch (root)
        {
            case Button button:
                StyleButton(button);
                break;
            case RichTextBox richText:
                richText.BackColor = Surface;
                richText.ForeColor = Text;
                richText.BorderStyle = BorderStyle.None;
                break;
        }

        foreach (Control child in root.Controls)
            Apply(child);
    }

    /// <summary>Marks a button as the screen's main action (filled with the accent color).</summary>
    public static T AsAccent<T>(this T button) where T : Button
    {
        AccentButtons.AddOrUpdate(button, true);
        button.Invalidate();
        return button;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 38;
        grid.AllowUserToResizeRows = false;
        grid.RowTemplate.Height = 34;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = SubtleText,
            SelectionBackColor = Surface,
            SelectionForeColor = SubtleText,
            Font = SemiboldFont,
            Padding = new Padding(8, 0, 8, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = Text,
            SelectionBackColor = Selection,
            SelectionForeColor = Text,
            Padding = new Padding(8, 0, 8, 0),
        };
    }

    /// <summary>Makes the title bar match the window (dark title bar, Windows 11 caption color).</summary>
    public static void ApplyTitleBar(Form form)
    {
        try
        {
            var dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var caption = ColorTranslator.ToWin32(Background);
            DwmSetWindowAttribute(form.Handle, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        catch
        {
            // Older Windows without these attributes: keep the default title bar.
        }
    }

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void StyleButton(Button button)
    {
        if (StyledButtons.TryGetValue(button, out _))
            return;
        StyledButtons.Add(button, true);

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Padding = new Padding(10, 2, 10, 2);
        button.MinimumSize = new Size(Math.Max(button.MinimumSize.Width, 84), 32);

        var hover = false;
        var pressed = false;
        var keyboardFocus = false;
        button.MouseEnter += (_, _) => { hover = true; button.Invalidate(); };
        button.MouseLeave += (_, _) => { hover = false; pressed = false; button.Invalidate(); };
        button.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { pressed = true; button.Invalidate(); } };
        button.MouseUp += (_, _) => { pressed = false; button.Invalidate(); };
        // Show the focus ring only when focus arrived via keyboard (Tab), as Windows 11 does.
        button.GotFocus += (_, _) => { keyboardFocus = Control.MouseButtons == MouseButtons.None; button.Invalidate(); };
        button.LostFocus += (_, _) => { keyboardFocus = false; button.Invalidate(); };
        button.EnabledChanged += (_, _) => button.Invalidate();
        button.Paint += (_, e) => PaintButton(button, e.Graphics, hover, pressed, keyboardFocus);
    }

    private static void PaintButton(Button button, Graphics g, bool hover, bool pressed, bool keyboardFocus)
    {
        var accent = AccentButtons.TryGetValue(button, out _);
        Color fill, text;
        if (!button.Enabled)
        {
            fill = accent ? AccentDisabled : ControlFill;
            text = accent ? (IsDark ? Hex(0xA0A0A0) : Color.White) : DisabledText;
        }
        else if (accent)
        {
            fill = pressed ? AccentPressed : hover ? AccentHover : Accent;
            text = TextOnAccent;
        }
        else
        {
            fill = pressed ? ControlPressed : hover ? ControlHover : ControlFill;
            text = pressed ? SubtleText : Text;
        }

        g.Clear(button.Parent?.BackColor ?? Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, button.Width - 1.5f, button.Height - 1.5f);
        using (var path = RoundedRect(bounds, 4))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (!accent || !button.Enabled)
            {
                using var pen = new Pen(ControlBorder);
                g.DrawPath(pen, path);
            }
        }
        if (keyboardFocus && button.Focused)
        {
            using var ring = RoundedRect(new RectangleF(1.5f, 1.5f, button.Width - 3.5f, button.Height - 3.5f), 3);
            using var pen = new Pen(Text, 2);
            g.DrawPath(pen, ring);
        }

        TextRenderer.DrawText(g, button.Text, button.Font, button.ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private static Color ReadAccentColor(bool dark)
    {
        try
        {
            // AccentPalette: 8 RGBA entries (Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, ...).
            // Windows 11 fills accent buttons with Light2 in dark mode and Dark1 in light mode.
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
            {
                var i = (dark ? 1 : 4) * 4;
                return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
            }
        }
        catch
        {
            // Fall through to the Windows 11 default blue.
        }
        return dark ? Hex(0x60CDFF) : Hex(0x005FB8);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
