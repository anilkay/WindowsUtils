using WindowsUtils.Utilities;

namespace WindowsUtils;

public class MainForm : Form
{
    private readonly ListBox _navList = new();
    private readonly Panel _contentPanel = new();
    private readonly Label _titleLabel = new();
    private readonly Dictionary<string, UserControl> _cache = new();

    private static readonly (string Name, Func<UserControl> Factory)[] Utilities =
    [
        ("System Information", () => new SystemInfoControl()),
        ("Disk Info", () => new DiskInfoControl()),
        ("Network Info", () => new NetworkInfoControl()),
        ("File Hash Calculator", () => new FileHashControl()),
        ("Environment Variables", () => new EnvironmentVariablesControl()),
        ("Startup Programs", () => new StartupProgramsControl()),
        ("Folder Size Analyzer", () => new FolderSizeControl()),
        ("Largest Files", () => new LargestFilesControl()),
        ("AI Chat", () => new ChatControl()),
    ];

    public MainForm()
    {
        Text = "Windows Utils";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1150, 720);
        Font = new Font("Segoe UI", 9F);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 240,
        };

        _navList.Dock = DockStyle.Fill;
        _navList.Font = new Font("Segoe UI", 10F);
        _navList.ItemHeight = 32;
        _navList.BorderStyle = BorderStyle.None;
        foreach (var (name, _) in Utilities)
            _navList.Items.Add(name);
        _navList.SelectedIndexChanged += OnNavigate;

        _titleLabel.Dock = DockStyle.Top;
        _titleLabel.Height = 44;
        _titleLabel.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Padding = new Padding(8, 0, 0, 0);

        _contentPanel.Dock = DockStyle.Fill;
        _contentPanel.Padding = new Padding(8, 4, 8, 8);

        split.Panel1.Controls.Add(_navList);
        // Added first -> docked last -> Fill takes the space remaining after the header.
        split.Panel2.Controls.Add(_contentPanel);
        split.Panel2.Controls.Add(_titleLabel);

        Controls.Add(split);

        _navList.SelectedIndex = 0;
    }

    private void OnNavigate(object? sender, EventArgs e)
    {
        var index = _navList.SelectedIndex;
        if (index < 0)
            return;

        var (name, factory) = Utilities[index];
        if (!_cache.TryGetValue(name, out var control))
        {
            control = factory();
            _cache[name] = control;
        }

        _contentPanel.Controls.Clear();
        _contentPanel.Controls.Add(control);
        _titleLabel.Text = name;
    }
}
