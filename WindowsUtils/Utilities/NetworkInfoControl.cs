using System.Text;
using WindowsUtils.Core.Net;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class NetworkInfoControl : UtilityControl
{
    private readonly Label _pingResult = new();

    public NetworkInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Adapter", L.T("Adapter"));
        grid.Columns.Add("Type", L.T("Type"));
        grid.Columns.Add("Status", L.T("Status"));
        grid.Columns.Add("IPv4", "IPv4");
        grid.Columns.Add("IPv6", "IPv6");
        grid.Columns.Add("MAC", "MAC");
        MakeUnsortable(grid);

        foreach (var adapter in NetworkInfo.GetAdapters())
            grid.Rows.Add(adapter.Name, adapter.Type, L.V(adapter.Status), adapter.IPv4, adapter.IPv6, adapter.Mac);

        var pingPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        var hostBox = new TextBox
        {
            PlaceholderText = L.T("Host to ping (e.g. 8.8.8.8)"),
            Width = 220,
            Margin = new Padding(4, 8, 4, 4),
        };
        var pingButton = new Button { Text = L.T("Ping"), AutoSize = true, Margin = new Padding(4, 6, 4, 4) }.AsAccent();
        _pingResult.AutoSize = true;
        _pingResult.Margin = new Padding(12, 10, 4, 4);

        pingButton.Click += async (_, _) => await PingAsync(hostBox.Text.Trim(), pingButton);

        pingPanel.Controls.Add(hostBox);
        pingPanel.Controls.Add(pingButton);
        pingPanel.Controls.Add(_pingResult);

        Controls.Add(grid);
        Controls.Add(pingPanel);
    }

    private async Task PingAsync(string host, Button button)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            _pingResult.Text = L.T("Enter a host first.");
            return;
        }

        button.Enabled = false;
        _pingResult.Text = L.F("Pinging {0}...", host);
        try
        {
            var builder = new StringBuilder();
            foreach (var reply in await NetworkInfo.PingAsync(host))
            {
                builder.AppendLine(reply.Success
                    ? L.F("Reply from {0}: time={1}ms", reply.Address, reply.RoundtripMs)
                    : L.F("Request failed: {0}", reply.Status));
            }
            _pingResult.Text = builder.ToString();
        }
        catch (Exception ex)
        {
            _pingResult.Text = L.F("Ping failed: {0}", ex.Message);
        }
        finally
        {
            button.Enabled = true;
        }
    }
}
