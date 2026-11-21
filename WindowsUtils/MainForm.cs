using System.Drawing.Drawing2D;
using WindowsUtils.Localization;
using WindowsUtils.Utilities;

namespace WindowsUtils;

public class MainForm : Form
{
    private readonly ListBox _navList = new();
    private readonly Panel _contentPanel = new();
    private readonly Label _titleLabel = new();
    private readonly Label _descriptionLabel = new();
    private readonly Dictionary<string, UserControl> _cache = new();
    private int _hoverIndex = -1;

    // Icon is a Segoe Fluent Icons / Segoe MDL2 Assets glyph.
    private static readonly (string Name, string Icon, string Description, Func<UserControl> Factory)[] Utilities =
    [
        (L.T("Folder Size Analyzer"), "\uE8B7", L.T("See what takes up space inside a folder."), () => new FolderSizeControl()),
        (L.T("Largest Files"), "\uE8A5", L.T("Find the biggest files on your drives."), () => new LargestFilesControl()),
        (L.T("Duplicate Files"), "\uE8C8", L.T("Find identical files and free up space."), () => new DuplicateFilesControl()),
        (L.T("Temp Cleaner"), "\uE74D", L.T("See and clean temporary and cache files."), () => new TempCleanerControl()),
        (L.T("System Information"), "\uE770", L.T("Operating system, hardware and memory at a glance."), () => new SystemInfoControl()),
        (L.T("Update History"), "\uE895", L.T("Installed, failed and removed Windows updates."), () => new UpdateHistoryControl()),
        (L.T("Event Log Summary"), "\uE7BA", L.T("Recent errors and warnings in the event logs, by source."), () => new EventLogControl()),
        (L.T("Disk Info"), "\uEDA2", L.T("Capacity and free space for every drive."), () => new DiskInfoControl()),
        (L.T("Disk Health"), "\uE9D9", L.T("Physical disk health and SMART reliability counters."), () => new DiskHealthControl()),
        (L.T("Network Info"), "\uE968", L.T("Network adapters, addresses and a quick ping."), () => new NetworkInfoControl()),
        (L.T("TCP Traceroute"), "\uE774", L.T("Trace the network path to a host over TCP."), () => new TcpTracerouteControl()),
        (L.T("Firewall Ports"), "\uE72E", L.T("TCP/UDP ports opened by enabled inbound firewall rules."), () => new FirewallPortsControl()),
        (L.T("Open Ports"), "\uE839", L.T("Listening ports and connections with the owning process."), () => new OpenPortsControl()),
        (L.T("TLS Settings"), "\uEA18", L.T("SSL/TLS versions enabled in Windows, and a server's TLS version and certificate."), () => new TlsSettingsControl()),
        (L.T("File Hash Calculator"), "\uE928", L.T("Compute and verify MD5, SHA-1, SHA-256 and SHA-512 hashes."), () => new FileHashControl()),
        (L.T("Environment Variables"), "\uE943", L.T("Browse user, machine and process variables."), () => new EnvironmentVariablesControl()),
        (L.T("Battery and Power"), "\uE83F", L.T("Battery health, charge state and the active power plan."), () => new BatteryControl()),
        (L.T("Installed Programs"), "\uE71D", L.T("Installed programs with size and install date."), () => new InstalledProgramsControl()),
        (L.T("Startup Programs"), "\uE7E8", L.T("Programs registered to start with Windows."), () => new StartupProgramsControl()),
        (L.T("Local Users"), "\uE716", L.T("Local user accounts, their status and last logon."), () => new LocalUsersControl()),
        (L.T("AI Chat"), "\uE8F2", L.T("Ask an AI assistant that can inspect this PC."), () => new ChatControl()),
    ];

    public MainForm()
    {
        Text = "Windows Utils";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 620);
        Size = new Size(1200, 760);
        Font = Theme.BodyFont;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;

        // ---- Sidebar: app name + navigation ----
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 264, Padding = new Padding(8, 0, 8, 8) };
        var appTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Text = "Windows Utils",
            Font = Theme.AppTitleFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
        };

        _navList.Dock = DockStyle.Fill;
        _navList.BorderStyle = BorderStyle.None;
        _navList.BackColor = Theme.Background;
        _navList.ForeColor = Theme.Text;
        _navList.DrawMode = DrawMode.OwnerDrawFixed;
        _navList.ItemHeight = 40;
        _navList.IntegralHeight = false;
        foreach (var utility in Utilities)
            _navList.Items.Add(utility.Name);
        _navList.DrawItem += DrawNavItem;
        _navList.MouseMove += (_, e) => SetHoverIndex(_navList.IndexFromPoint(e.Location));
        _navList.MouseLeave += (_, _) => SetHoverIndex(-1);
        _navList.SelectedIndexChanged += OnNavigate;

        sidebar.Controls.Add(_navList);
        sidebar.Controls.Add(appTitle);
        sidebar.Controls.Add(CreateLanguagePanel());

        // ---- Content: page header + selected utility ----
        var contentArea = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 20) };

        _titleLabel.Dock = DockStyle.Top;
        _titleLabel.Height = 44;
        _titleLabel.Font = Theme.TitleFont;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;

        _descriptionLabel.Dock = DockStyle.Top;
        _descriptionLabel.Height = 36;
        _descriptionLabel.ForeColor = Theme.SubtleText;
        _descriptionLabel.TextAlign = ContentAlignment.TopLeft;

        _contentPanel.Dock = DockStyle.Fill;

        // Added first -> docked last -> Fill takes the space remaining after the header.
        contentArea.Controls.Add(_contentPanel);
        contentArea.Controls.Add(_descriptionLabel);
        contentArea.Controls.Add(_titleLabel);

        Controls.Add(contentArea);
        Controls.Add(sidebar);

        _navList.SelectedIndex = 0;
    }

    // Language picker at the bottom of the sidebar: "Windows default" or a translated language.
    // The UI culture is set once at startup, so a change asks to restart the app.
    private Control CreateLanguagePanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 4, 0, 0),
        };
        var icon = new Label
        {
            Text = "",
            Font = Theme.IconFont,
            AutoSize = true,
            Margin = new Padding(4, 8, 4, 0),
        };
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 196, Margin = new Padding(4, 3, 0, 0) };

        var saved = LanguageSettings.Load().ValueOrDefault;
        var system = LanguageSettings.SystemUICulture;
        // The language without the region ("English", not "English (United States)"), so it fits the box.
        var systemName = system.IsNeutralCulture || system.Parent == System.Globalization.CultureInfo.InvariantCulture
            ? system.NativeName
            : system.Parent.NativeName;
        box.Items.Add(L.F("Windows default ({0})", systemName));
        foreach (var (_, nativeName) in LanguageSettings.Supported)
            box.Items.Add(nativeName);
        box.SelectedIndex = saved is null ? 0 : 1 + Array.FindIndex(LanguageSettings.Supported, s => s.Culture == saved);

        box.SelectionChangeCommitted += (_, _) =>
        {
            var language = box.SelectedIndex <= 0 ? null : LanguageSettings.Supported[box.SelectedIndex - 1].Culture;
            if (language == saved)
                return;
            var result = LanguageSettings.Save(language);
            if (result.IsFailed)
            {
                MessageBox.Show(result.Errors[0].Message, L.T("Language"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            saved = language;
            if (MessageBox.Show(L.T("The new language is used after the app restarts. Restart now?"), L.T("Language"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Application.Restart();
            }
        };

        panel.Controls.Add(icon);
        panel.Controls.Add(box);
        return panel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private void SetHoverIndex(int index)
    {
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        _navList.Invalidate();
    }

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        var g = e.Graphics;
        using (var background = new SolidBrush(Theme.Background))
            g.FillRectangle(background, e.Bounds);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var item = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4);
        if (selected || e.Index == _hoverIndex)
        {
            using var path = Theme.RoundedRect(item, 4);
            using var brush = new SolidBrush(selected ? Theme.NavSelected : Theme.NavHover);
            g.FillPath(brush, path);
        }
        if (selected)
        {
            // Accent "pill" on the left of the selected item, as in Windows 11 Settings.
            using var pill = Theme.RoundedRect(new RectangleF(item.X, item.Y + (item.Height - 16) / 2f, 3, 16), 1.5f);
            using var brush = new SolidBrush(Theme.Accent);
            g.FillPath(brush, pill);
        }

        var (name, icon, _, _) = Utilities[e.Index];
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, icon, Theme.IconFont, new Rectangle(item.X + 14, item.Y, 24, item.Height), Theme.Text,
            flags | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, name, Theme.BodyFont, new Rectangle(item.X + 50, item.Y, item.Width - 54, item.Height), Theme.Text,
            flags | TextFormatFlags.EndEllipsis);
    }

    private void OnNavigate(object? sender, EventArgs e)
    {
        var index = _navList.SelectedIndex;
        if (index < 0)
            return;

        var (name, _, description, factory) = Utilities[index];
        if (!_cache.TryGetValue(name, out var control))
        {
            control = factory();
            Theme.Apply(control);
            _cache[name] = control;
        }

        _contentPanel.Controls.Clear();
        _contentPanel.Controls.Add(control);
        _titleLabel.Text = name;
        _descriptionLabel.Text = description;
        _navList.Invalidate();
    }
}
