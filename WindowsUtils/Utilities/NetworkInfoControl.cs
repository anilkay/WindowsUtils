using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WindowsUtils.Utilities;

public class NetworkInfoControl : UtilityControl
{
    private readonly Label _pingResult = new();

    public NetworkInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Adapter", "Adapter");
        grid.Columns.Add("Type", "Type");
        grid.Columns.Add("Status", "Status");
        grid.Columns.Add("IPv4", "IPv4");
        grid.Columns.Add("IPv6", "IPv6");
        grid.Columns.Add("MAC", "MAC");
        MakeUnsortable(grid);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            var ipv4 = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "";
            var ipv6 = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                ?.Address.ToString() ?? "";

            grid.Rows.Add(
                nic.Name,
                nic.NetworkInterfaceType.ToString(),
                nic.OperationalStatus.ToString(),
                ipv4,
                ipv6,
                FormatMac(nic.GetPhysicalAddress()));
        }

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
            PlaceholderText = "Host to ping (e.g. 8.8.8.8)",
            Width = 220,
            Margin = new Padding(4, 8, 4, 4),
        };
        var pingButton = new Button { Text = "Ping", AutoSize = true, Margin = new Padding(4, 6, 4, 4) }.AsAccent();
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
            _pingResult.Text = "Enter a host first.";
            return;
        }

        button.Enabled = false;
        _pingResult.Text = $"Pinging {host}...";
        try
        {
            using var ping = new Ping();
            var builder = new StringBuilder();
            for (var i = 0; i < 4; i++)
            {
                var reply = await ping.SendPingAsync(host, 3000);
                builder.AppendLine(reply.Status == IPStatus.Success
                    ? $"Reply from {reply.Address}: time={reply.RoundtripTime}ms"
                    : $"Request failed: {reply.Status}");
            }
            _pingResult.Text = builder.ToString();
        }
        catch (Exception ex)
        {
            _pingResult.Text = $"Ping failed: {ex.Message}";
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var value = mac.ToString();
        if (value.Length != 12)
            return value;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => value.Substring(i * 2, 2)));
    }
}
