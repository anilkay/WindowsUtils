using WindowsUtils.Core.Net;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class TcpTracerouteControl : UtilityControl
{
    private readonly TextBox _hostBox = new() { PlaceholderText = L.T("Host (e.g. google.com)"), Width = 260 };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 80, Width = 70 };
    private readonly NumericUpDown _hopsBox = new() { Minimum = 1, Maximum = 64, Value = 30, Width = 55 };
    private readonly NumericUpDown _timeoutBox = new() { Minimum = 500, Maximum = 10000, Increment = 500, Value = 3000, Width = 75 };
    private readonly CheckBox _dnsCheck = new() { Text = L.T("Hostnames"), Checked = true, AutoSize = true };
    private readonly Button _traceButton = new() { Text = L.T("Trace"), AutoSize = true };
    private readonly Button _cancelButton = new() { Text = L.T("Cancel"), AutoSize = true, Enabled = false };
    private readonly DataGridView _grid;
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _cts;

    public TcpTracerouteControl()
    {
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 84,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4),
        };

        topPanel.Controls.Add(new Label { Text = L.T("Host:"), AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _hostBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_hostBox);
        topPanel.Controls.Add(new Label { Text = L.T("Port:"), AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _portBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_portBox);
        topPanel.Controls.Add(new Label { Text = L.T("Hops:"), AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _hopsBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_hopsBox);
        topPanel.Controls.Add(new Label { Text = L.T("Timeout (ms):"), AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _timeoutBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_timeoutBox);
        _dnsCheck.Margin = new Padding(4, 10, 4, 4);
        topPanel.Controls.Add(_dnsCheck);
        // Deterministic two-row layout: inputs on row 1, actions on row 2.
        topPanel.SetFlowBreak(_dnsCheck, true);
        _traceButton.Margin = new Padding(4, 6, 4, 4);
        _cancelButton.Margin = new Padding(4, 6, 4, 4);
        topPanel.Controls.Add(_traceButton.AsAccent());
        topPanel.Controls.Add(_cancelButton);

        _traceButton.Click += async (_, _) => await TraceAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        _grid = CreateGrid();
        _grid.Columns.Add("Hop", L.T("Hop"));
        _grid.Columns.Add("Address", L.T("IP Address"));
        _grid.Columns.Add("Hostname", L.T("Hostname"));
        _grid.Columns.Add("Rtt1", "RTT 1");
        _grid.Columns.Add("Rtt2", "RTT 2");
        _grid.Columns.Add("Rtt3", "RTT 3");
        MakeUnsortable(_grid);

        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);
        topPanel.Controls.Add(_statusLabel);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = L.T("TCP traceroute needs Administrator privileges (raw socket for ICMP replies)."),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        Controls.Add(_grid);
        Controls.Add(note);
        Controls.Add(topPanel);
    }

    private async Task TraceAsync()
    {
        var host = _hostBox.Text.Trim();
        if (host.Length == 0)
        {
            _statusLabel.Text = L.T("Enter a host first.");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _traceButton.Enabled = false;
        _cancelButton.Enabled = true;
        _grid.Rows.Clear();

        var maxHops = (int)_hopsBox.Value;
        var progress = new Progress<TracerouteHop>(hop =>
        {
            _grid.Rows.Add(hop.Ttl, hop.Address ?? "*", hop.HostName ?? "",
                Rtt(hop.Rtt1Ms), Rtt(hop.Rtt2Ms), Rtt(hop.Rtt3Ms));
            _statusLabel.Text = L.F("Tracing... hop {0}/{1}", hop.Ttl, maxHops);
        });

        try
        {
            var hops = await TcpTraceroute.TraceAsync(host, (int)_portBox.Value, maxHops,
                (int)_timeoutBox.Value, _dnsCheck.Checked, progress, token);
            if (hops.Count == 0)
            {
                _statusLabel.Text = L.T("No hops.");
            }
            else
            {
                var last = hops[^1];
                _statusLabel.Text = last.Note == "Reached"
                    ? L.F("Reached {0} in {1} hop(s).", last.Address, hops.Count)
                    : L.F("Finished: target not reached within {0} hop(s).", hops.Count);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = L.T("Cancelled.");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = L.F("Error: {0}", ex.Message);
        }
        finally
        {
            _traceButton.Enabled = true;
            _cancelButton.Enabled = false;
        }
    }

    private static string Rtt(long? ms) => ms.HasValue ? $"{ms.Value} ms" : "*";
}
