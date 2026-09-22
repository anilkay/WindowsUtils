using System.Text.Json;
using WindowsUtils.AI;

namespace WindowsUtils.Utilities;

/// <summary>Thin UI shell for AI Chat. All agent, tool, settings and credential
/// logic lives in WindowsUtils.AI; this control only builds the UI and displays results.</summary>
public class ChatControl : UtilityControl
{
    private readonly TextBox _endpointBox = new() { Text = "https://api.openai.com/v1", Width = 280 };
    private readonly TextBox _modelBox = new() { Text = "gpt-4o-mini", Width = 150 };
    private readonly TextBox _apiKeyBox = new() { Width = 200, UseSystemPasswordChar = true, PlaceholderText = "API key" };
    private readonly ComboBox _reasoningBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Button _sendButton = new() { Text = "Send", Width = 90 };
    private readonly Button _stopButton = new() { Text = "Stop", Width = 90, Enabled = false };
    private readonly Button _newChatButton = new() { Text = "New chat", AutoSize = true };
    private readonly Button _clearButton = new() { Text = "Clear", AutoSize = true };
    private readonly Button _exportButton = new() { Text = "Export...", AutoSize = true };
    private readonly Button _forgetButton = new() { Text = "Forget", AutoSize = true };
    private readonly CheckBox _rememberCheck = new() { Text = "Remember", Checked = true, AutoSize = true };
    private readonly RichTextBox _transcript = new();
    private readonly TextBox _inputBox = new();
    private readonly Label _statusLabel = new();

    private ChatSession? _chatSession;
    private string? _sessionCacheKey;
    private CancellationTokenSource? _cts;

    public ChatControl()
    {
        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 104,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4),
        };

        _endpointBox.Margin = new Padding(4, 6, 4, 4);
        _modelBox.Margin = new Padding(4, 6, 4, 4);
        _apiKeyBox.Margin = new Padding(4, 6, 4, 4);
        _reasoningBox.Margin = new Padding(4, 6, 4, 4);
        _newChatButton.Margin = new Padding(4, 5, 4, 4);
        _clearButton.Margin = new Padding(4, 5, 4, 4);
        _exportButton.Margin = new Padding(4, 5, 4, 4);
        _forgetButton.Margin = new Padding(4, 5, 4, 4);
        _rememberCheck.Margin = new Padding(4, 8, 4, 4);

        settingsPanel.Controls.Add(new Label { Text = "Endpoint:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_endpointBox);
        settingsPanel.Controls.Add(new Label { Text = "Model:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_modelBox);
        settingsPanel.Controls.Add(new Label { Text = "API key:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_apiKeyBox);
        _reasoningBox.Items.AddRange(["Default", "Minimal", "Low", "Medium", "High"]);
        _reasoningBox.SelectedIndex = 2;
        settingsPanel.Controls.Add(new Label { Text = "Reasoning:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_reasoningBox);
        settingsPanel.Controls.Add(_newChatButton);
        settingsPanel.Controls.Add(_clearButton);
        settingsPanel.Controls.Add(_exportButton);
        settingsPanel.Controls.Add(_rememberCheck);
        settingsPanel.Controls.Add(_forgetButton);

        _transcript.Dock = DockStyle.Fill;
        _transcript.ReadOnly = true;
        _transcript.BackColor = SystemColors.Window;
        _transcript.ScrollBars = RichTextBoxScrollBars.Vertical;
        _transcript.Font = new Font("Segoe UI", 9.5F);
        _transcript.Text = "Enter your endpoint, model and API key above, then ask anything.\n"
            + "The agent can inspect this PC via tools (system info, processes, drives, files).\n\n";

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(4) };
        _inputBox.Dock = DockStyle.Fill;
        _inputBox.Multiline = true;
        _inputBox.ScrollBars = ScrollBars.Vertical;
        _inputBox.PlaceholderText = "Type a message... (Enter to send, Shift+Enter for newline)";
        _inputBox.KeyDown += OnInputKeyDown;
        _sendButton.Dock = DockStyle.Right;
        _stopButton.Dock = DockStyle.Right;
        _sendButton.Click += async (_, _) => await SendAsync();
        _stopButton.Click += (_, _) => _cts?.Cancel();
        _newChatButton.Click += (_, _) => NewChat();
        _clearButton.Click += (_, _) => _transcript.Clear();
        _exportButton.Click += (_, _) => ExportChat();
        _forgetButton.Click += (_, _) => ForgetSettings();
        bottomPanel.Controls.Add(_inputBox);
        bottomPanel.Controls.Add(_stopButton);
        bottomPanel.Controls.Add(_sendButton);

        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = 22;
        _statusLabel.Text = "Not connected.";
        _statusLabel.Padding = new Padding(4, 0, 0, 0);

        Controls.Add(_transcript);
        Controls.Add(_statusLabel);
        Controls.Add(bottomPanel);
        Controls.Add(settingsPanel);

        LoadPersistedSettings();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            _ = SendAsync();
        }
    }

    private void NewChat()
    {
        _cts?.Cancel();
        _chatSession?.Reset();
        _transcript.Clear();
        _statusLabel.Text = "New conversation started.";
    }

    private void LoadPersistedSettings()
    {
        try
        {
            var settings = ChatSettingsStore.Load();
            if (settings is not null)
            {
                if (!string.IsNullOrWhiteSpace(settings.Endpoint))
                    _endpointBox.Text = settings.Endpoint;
                if (!string.IsNullOrWhiteSpace(settings.Model))
                    _modelBox.Text = settings.Model;
                if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort) && _reasoningBox.Items.Contains(settings.ReasoningEffort))
                    _reasoningBox.SelectedItem = settings.ReasoningEffort;
            }
            var key = CredentialStore.Load();
            if (key is not null)
            {
                _apiKeyBox.Text = key;
                _statusLabel.Text = "Loaded saved settings.";
            }
        }
        catch
        {
            // Corrupt settings file: ignore and start fresh.
        }
    }

    private void SaveSettings()
    {
        try
        {
            ChatSettingsStore.Save(new ChatSettings(
                _endpointBox.Text.Trim(),
                _modelBox.Text.Trim(),
                _reasoningBox.SelectedItem as string));
            var key = _apiKeyBox.Text.Trim();
            if (key.Length > 0)
                CredentialStore.Save(key);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Could not save settings: {ex.Message}";
        }
    }

    private void ForgetSettings()
    {
        try
        {
            ChatSettingsStore.Delete();
        }
        catch
        {
        }
        CredentialStore.Delete();
        _apiKeyBox.Clear();
        _chatSession = null;
        _sessionCacheKey = null;
        _statusLabel.Text = "Saved settings forgotten.";
    }

    private void ExportChat()
    {
        if (string.IsNullOrWhiteSpace(_transcript.Text))
        {
            _statusLabel.Text = "Nothing to export.";
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|Markdown files (*.md)|*.md|All files (*.*)|*.*",
            DefaultExt = "txt",
            FileName = $"chat-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            var header = $"WindowsUtils AI Chat export — {DateTime.Now:F}\n"
                + $"Model: {_modelBox.Text.Trim()}\n"
                + new string('=', 40) + "\n\n";
            File.WriteAllText(dialog.FileName, header + _transcript.Text);
            _statusLabel.Text = $"Chat exported to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not export chat: {ex.Message}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private ReasoningEffort SelectedEffort() =>
        Enum.TryParse<ReasoningEffort>(_reasoningBox.SelectedItem as string, out var effort)
            ? effort
            : ReasoningEffort.Default;

    private ChatSession EnsureSession()
    {
        var endpoint = _endpointBox.Text.Trim().TrimEnd('/');
        var model = _modelBox.Text.Trim();
        var key = _apiKeyBox.Text.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Enter a model name (e.g. gpt-4o-mini).");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Enter an API key.");

        var effort = SelectedEffort();
        var cacheKey = $"{endpoint}|{model}|{key}|{effort}";
        if (_chatSession is not null && cacheKey == _sessionCacheKey)
            return _chatSession;

        _chatSession = ChatSession.Create(new ChatAgentOptions(endpoint, model, key, effort));
        _sessionCacheKey = cacheKey;
        return _chatSession;
    }

    private async Task SendAsync()
    {
        var prompt = _inputBox.Text.Trim();
        if (prompt.Length == 0)
            return;

        ChatSession session;
        try
        {
            session = EnsureSession();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AI Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_rememberCheck.Checked)
            SaveSettings();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _sendButton.Enabled = false;
        _stopButton.Enabled = true;
        _inputBox.Clear();
        _statusLabel.Text = "Thinking... (the agent may call tools)";
        AppendText($"\nYou:\n{prompt}\n\nAssistant:\n");

        try
        {
            try
            {
                await StreamAsync(session, prompt, token);
            }
            catch (Exception ex) when (ChatSession.IsReasoningEffortError(ex) && SelectedEffort() != ReasoningEffort.Default)
            {
                // Model rejects reasoning_effort: drop it and retry once instead of failing.
                _reasoningBox.SelectedItem = "Default";
                if (_rememberCheck.Checked)
                    SaveSettings();
                session.DisableReasoningEffort();
                AppendText("\n(model does not support reasoning_effort — retrying without it)\n");
                _statusLabel.Text = "Retrying without reasoning_effort...";
                await StreamAsync(session, prompt, token);
            }
            AppendText("\n");
            _statusLabel.Text = "Ready.";
        }
        catch (OperationCanceledException)
        {
            AppendText("\n(cancelled)\n");
            _statusLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            // A failed turn can leave the session in an unusable state (e.g. a 400
            // mid-run); drop it so the next send starts fresh instead of failing again.
            session.Reset();
            if (ChatSession.IsUnauthorizedError(ex))
                AppendText("\n(Authorization failed: the endpoint rejected the API key (HTTP 401/403). Check that the key is correct and belongs to this endpoint/model.)\n");
            else
                AppendText($"\n(error: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace})\n");
            _statusLabel.Text = "Error — check endpoint/model/key.";
        }
        finally
        {
            _sendButton.Enabled = true;
            _stopButton.Enabled = false;
        }
    }

    private async Task StreamAsync(ChatSession session, string prompt, CancellationToken token)
    {
        await foreach (var text in session.StreamResponseAsync(prompt, token))
            AppendText(text);
    }

    private void AppendText(string text)
    {
        _transcript.AppendText(text);
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.ScrollToCaret();
    }
}
