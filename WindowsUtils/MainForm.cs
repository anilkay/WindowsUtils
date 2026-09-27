using System.Drawing.Drawing2D;
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
        ("System Information", "\uE770", "Operating system, hardware and memory at a glance.", () => new SystemInfoControl()),
        ("Disk Info", "\uEDA2", "Capacity and free space for every drive.", () => new DiskInfoControl()),
        ("Network Info", "\uE968", "Network adapters, addresses and a quick ping.", () => new NetworkInfoControl()),
        ("File Hash Calculator", "\uE928", "Compute and verify MD5, SHA-1, SHA-256 and SHA-512 hashes.", () => new FileHashControl()),
        ("Environment Variables", "\uE943", "Browse user, machine and process variables.", () => new EnvironmentVariablesControl()),
        ("Startup Programs", "\uE7E8", "Programs registered to start with Windows.", () => new StartupProgramsControl()),
        ("Local Users", "\uE716", "Local user accounts, their status and last logon.", () => new LocalUsersControl()),
        ("Folder Size Analyzer", "\uE8B7", "See what takes up space inside a folder.", () => new FolderSizeControl()),
        ("Largest Files", "\uE8A5", "Find the biggest files on your drives.", () => new LargestFilesControl()),
        ("Duplicate Files", "\uE8C8", "Find identical files and free up space.", () => new DuplicateFilesControl()),
        ("AI Chat", "\uE8F2", "Ask an AI assistant that can inspect this PC.", () => new ChatControl()),
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
