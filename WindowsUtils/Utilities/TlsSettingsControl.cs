using WindowsUtils.Core.Net;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

// Two parts on one page: the Schannel protocol switches of this PC (registry or OS default, client
// and server side), and a live TLS handshake with a host:port to see what a server really negotiates.
public class TlsSettingsControl : UtilityControl
{
    private readonly DataGridView _protocolGrid;
    private readonly Button _refreshButton = new() { Text = L.T("Refresh"), AutoSize = true };
    private readonly Label _protocolStatus = new();

    private readonly TextBox _hostBox = new() { PlaceholderText = L.T("Host (e.g. google.com)"), Width = 260 };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 443, Width = 75 };
    private readonly Button _checkButton = new() { Text = L.T("Check"), AutoSize = true };
    private readonly Button _cancelButton = new() { Text = L.T("Cancel"), AutoSize = true, Enabled = false };
    private readonly Label _checkStatus = new();
    private readonly DataGridView _detailsGrid;
    private readonly DataGridView _chainGrid;
    private CancellationTokenSource? _cts;

    public TlsSettingsControl()
    {
        // ---- Part 1: Schannel protocols ----
        var protocolBar = Bar();
        protocolBar.Controls.Add(Heading(L.T("Protocols enabled in Windows (Schannel)")));
        _refreshButton.Margin = new Padding(12, 6, 4, 4);
        _refreshButton.Click += (_, _) => LoadProtocols();
        protocolBar.Controls.Add(_refreshButton);
        _protocolStatus.AutoSize = true;
        _protocolStatus.Margin = new Padding(12, 12, 4, 4);
        protocolBar.Controls.Add(_protocolStatus);

        _protocolGrid = CreateGrid();
        _protocolGrid.Columns.Add("Protocol", L.T("Protocol"));
        _protocolGrid.Columns.Add("Client", L.T("Client"));
        _protocolGrid.Columns.Add("Server", L.T("Server"));
        MakeUnsortable(_protocolGrid);

        // ---- Part 2: TLS check of a server ----
        var checkBar = Bar();
        checkBar.Controls.Add(Heading(L.T("Check a server")));
        checkBar.Controls.Add(new Label { Text = L.T("Host:"), AutoSize = true, Margin = new Padding(12, 9, 0, 4) });
        _hostBox.Margin = new Padding(4, 8, 4, 4);
        checkBar.Controls.Add(_hostBox);
        checkBar.Controls.Add(new Label { Text = L.T("Port:"), AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        _portBox.Margin = new Padding(4, 8, 4, 4);
        checkBar.Controls.Add(_portBox);
        _checkButton.Margin = new Padding(8, 6, 4, 4);
        _cancelButton.Margin = new Padding(4, 6, 4, 4);
        checkBar.Controls.Add(_checkButton.AsAccent());
        checkBar.Controls.Add(_cancelButton);
        // Own row under the inputs: handshake errors are long and would be cut off next to the buttons.
        _checkStatus.Dock = DockStyle.Fill;
        _checkStatus.AutoEllipsis = true;
        _checkStatus.TextAlign = ContentAlignment.MiddleLeft;
        _checkStatus.Padding = new Padding(8, 0, 0, 0);

        _checkButton.Click += async (_, _) => await CheckAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _hostBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            await CheckAsync();
        };

        _detailsGrid = CreateGrid();
        _detailsGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _detailsGrid.Columns.Add("Property", L.T("Property"));
        _detailsGrid.Columns.Add("Value", L.T("Value"));
        _detailsGrid.Columns[0].FillWeight = 30;
        _detailsGrid.Columns[1].FillWeight = 70;
        MakeUnsortable(_detailsGrid);

        _chainGrid = CreateGrid();
        _chainGrid.Columns.Add("Index", "#");
        _chainGrid.Columns.Add("Subject", L.T("Subject"));
        _chainGrid.Columns.Add("Issuer", L.T("Issuer"));
        _chainGrid.Columns.Add("Expires", L.T("Expires"));
        _chainGrid.Columns.Add("Status", L.T("Status"));
        _chainGrid.Columns.Add("Thumbprint", L.T("Thumbprint"));
        _chainGrid.Columns[0].FillWeight = 8;
        MakeUnsortable(_chainGrid);

        var note = new Label
        {
            Dock = DockStyle.Fill,
            Text = L.T("Read-only. The check uses this PC's TLS settings, sends no data after the handshake and does not check revocation."),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        // Rows: protocol bar, protocol grid, check bar, check status, details, chain, note.
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        // Header + five protocol rows, with room for the row borders so no scroll bar appears.
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38 + 5 * 34 + 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(protocolBar, 0, 0);
        layout.Controls.Add(_protocolGrid, 0, 1);
        layout.Controls.Add(checkBar, 0, 2);
        layout.Controls.Add(_checkStatus, 0, 3);
        layout.Controls.Add(_detailsGrid, 0, 4);
        layout.Controls.Add(_chainGrid, 0, 5);
        layout.Controls.Add(note, 0, 6);
        Controls.Add(layout);

        LoadProtocols();
    }

    private static FlowLayoutPanel Bar() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Padding = new Padding(4, 4, 4, 0),
        Margin = Padding.Empty,
    };

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.SemiboldFont,
        Margin = new Padding(4, 12, 4, 4),
    };

    private void LoadProtocols()
    {
        _protocolGrid.Rows.Clear();
        var result = SchannelProtocols.Get();
        if (result.IsFailed)
        {
            _protocolStatus.Text = result.Errors[0].Message;
            return;
        }

        foreach (var protocol in result.Value.GroupBy(s => s.Protocol))
        {
            var client = protocol.First(s => s.Side == "Client");
            var server = protocol.First(s => s.Side == "Server");
            var row = _protocolGrid.Rows[_protocolGrid.Rows.Add(protocol.Key, Describe(client), Describe(server))];
            Highlight(row.Cells[1], client.NeedsAttention);
            Highlight(row.Cells[2], server.NeedsAttention);
        }

        var attention = result.Value.Count(s => s.NeedsAttention);
        _protocolStatus.Text = attention == 0
            ? L.T("Only current protocols are enabled.")
            : L.F("{0} setting(s) need attention: an outdated protocol is on or a current one is off.", attention);
    }

    private static string Describe(SchannelProtocolSetting setting) =>
        L.F("{0} ({1})", L.V(setting.State), L.V(setting.Source));

    private static void Highlight(DataGridViewCell cell, bool attention)
    {
        if (attention)
            cell.Style.ForeColor = Theme.Danger;
    }

    private async Task CheckAsync()
    {
        var host = _hostBox.Text.Trim();
        var valid = TlsProbe.Validate(host, (int)_portBox.Value);
        if (valid.IsFailed)
        {
            _checkStatus.Text = host.Length == 0 ? L.T("Enter a host first.") : L.F("Not a valid host name or IP address: {0}", host);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _checkButton.Enabled = false;
        _cancelButton.Enabled = true;
        _detailsGrid.Rows.Clear();
        _chainGrid.Rows.Clear();
        _checkStatus.Text = L.F("Connecting to {0}:{1}...", valid.Value.Host, valid.Value.Port);

        try
        {
            var result = await TlsProbe.ProbeAsync(valid.Value.Host, valid.Value.Port, 10000, token);
            if (result.IsFailed)
            {
                _checkStatus.Text = result.Errors[0].Message;
                return;
            }

            Show(result.Value);
        }
        catch (OperationCanceledException)
        {
            _checkStatus.Text = L.T("Cancelled.");
        }
        finally
        {
            _checkButton.Enabled = true;
            _cancelButton.Enabled = false;
        }
    }

    private void Show(TlsProbeResult r)
    {
        AddDetail(L.T("Endpoint"), $"{r.Host}:{r.Port} ({r.RemoteAddress})");
        AddDetail(L.T("TLS version"), r.Protocol, r.Protocol is "SSL 3.0" or "TLS 1.0" or "TLS 1.1");
        AddDetail(L.T("Cipher suite"), r.CipherSuite);
        AddDetail(L.T("Subject"), r.Subject);
        AddDetail(L.T("Issuer"), r.Issuer);
        AddDetail(L.T("DNS names"), r.DnsNames.Count == 0 ? L.T("(none)") : string.Join(", ", r.DnsNames));
        AddDetail(L.T("Valid from"), r.NotBefore.ToString("g"));
        AddDetail(L.T("Expires"),
            r.DaysLeft < 0
                ? L.F("{0} (expired {1} days ago)", r.NotAfter.ToString("g"), -r.DaysLeft)
                : L.F("{0} ({1} days left)", r.NotAfter.ToString("g"), r.DaysLeft),
            r.DaysLeft < 30);
        AddDetail(L.T("Validation"), r.ValidationErrors == "None" ? L.T("No errors") : r.ValidationErrors,
            r.ValidationErrors != "None");

        for (var i = 0; i < r.Chain.Count; i++)
        {
            var c = r.Chain[i];
            var row = _chainGrid.Rows[_chainGrid.Rows.Add(i + 1, c.Subject, c.Issuer, c.NotAfter.ToString("d"),
                c.Status == "OK" ? L.T("OK") : c.Status, c.Thumbprint)];
            Highlight(row.Cells[4], c.Status != "OK");
        }

        _checkStatus.Text = r.DaysLeft < 0
            ? L.F("{0} with {1}, the certificate has expired.", r.Protocol, r.CipherSuite)
            : L.F("{0} with {1}, certificate expires in {2} days.", r.Protocol, r.CipherSuite, r.DaysLeft);
    }

    private void AddDetail(string name, string value, bool attention = false)
    {
        var row = _detailsGrid.Rows[_detailsGrid.Rows.Add(name, value)];
        Highlight(row.Cells[1], attention);
    }
}
