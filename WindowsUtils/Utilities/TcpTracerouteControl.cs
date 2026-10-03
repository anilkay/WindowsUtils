using WindowsUtils.Core.Net;

namespace WindowsUtils.Utilities;

public class TcpTracerouteControl : UtilityControl
{
    private readonly TextBox _hostBox = new() { PlaceholderText = "Host (e.g. google.com)", Width = 260 };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 80, Width = 70 };
    private readonly NumericUpDown _hopsBox = new() { Minimum = 1, Maximum = 64, Value = 30, Width = 55 };
    private readonly NumericUpDown _timeoutBox = new() { Minimum = 500, Maximum = 10000, Increment = 500, Value = 3000, Width = 75 };
    private readonly CheckBox _dnsCheck = new() { Text = "Hostnames", Checked = true, AutoSize = true };
    private readonly Button _traceButton = new() { Text = "Trace", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
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

        topPanel.Controls.Add(new Label { Text = "Host:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _hostBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_hostBox);
        topPanel.Controls.Add(new Label { Text = "Port:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _portBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_portBox);
        topPanel.Controls.Add(new Label { Text = "Hops:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _hopsBox.Margin = new Padding(4, 8, 4, 4);
        topPanel.Controls.Add(_hopsBox);
        topPanel.Controls.Add(new Label { Text = "Timeout (ms):", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
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
        _grid.Columns.Add("Hop", "Hop");
        _grid.Columns.Add("Address", "IP Address");
        _grid.Columns.Add("Hostname", "Hostname");
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
            Text = "TCP traceroute needs Administrator privileges (raw socket for ICMP replies).",
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
            _statusLabel.Text = "Enter a host first.";
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
            _statusLabel.Text = $"Tracing... hop {hop.Ttl}/{maxHops}";
        });

        try
        {
            var hops = await TcpTraceroute.TraceAsync(host, (int)_portBox.Value, maxHops,
                (int)_timeoutBox.Value, _dnsCheck.Checked, progress, token);
            if (hops.Count == 0)
            {
                _statusLabel.Text = "No hops.";
            }
            else
            {
                var last = hops[^1];
                _statusLabel.Text = last.Note == "Reached"
                    ? $"Reached {last.Address} in {hops.Count} hop(s)."
                    : $"Finished: target not reached within {hops.Count} hop(s).";
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _traceButton.Enabled = true;
            _cancelButton.Enabled = false;
        }
    }

    private static string Rtt(long? ms) => ms.HasValue ? $"{ms.Value} ms" : "*";
}
