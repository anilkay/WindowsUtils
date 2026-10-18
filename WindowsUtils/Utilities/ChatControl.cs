using System.Text.Json;
using Microsoft.Extensions.Logging;
using WindowsUtils.AI;
using WindowsUtils.Core.Logging;

namespace WindowsUtils.Utilities;

/// <summary>Thin UI shell for AI Chat. All agent, tool, settings and credential
/// logic lives in WindowsUtils.AI; this control only builds the UI and displays results.</summary>
public class ChatControl : UtilityControl
{
    private static ILogger Log => AppLog.Logger<ChatControl>();

    private readonly TextBox _endpointBox = new() { Text = "https://api.openai.com/v1", Width = 260 };
    // Editable drop-down: filled from the endpoint's /models list, but any name can still be typed.
    private readonly ComboBox _modelBox = new() { Text = "gpt-4o-mini", Width = 220, DropDownStyle = ComboBoxStyle.DropDown, MaxDropDownItems = 20 };
    private readonly Button _loadModelsButton = new() { Text = "Load models", AutoSize = true };
    private readonly TextBox _apiKeyBox = new() { Width = 180, UseSystemPasswordChar = true, PlaceholderText = "API key" };
    private readonly ComboBox _reasoningBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Button _sendButton = new() { Text = "Send", Width = 90 };
    private readonly Button _stopButton = new() { Text = "Stop", Width = 90, Enabled = false };
    private readonly Button _newChatButton = new() { Text = "New chat", AutoSize = true };
    private readonly Button _clearButton = new() { Text = "Clear", AutoSize = true };
    private readonly Button _exportButton = new() { Text = "Export...", AutoSize = true };
    private readonly Button _forgetButton = new() { Text = "Forget", AutoSize = true };
    private readonly Button _mcpButton = new() { Text = "Start MCP server", AutoSize = true };
    private readonly CheckBox _rememberCheck = new() { Text = "Remember", Checked = true, AutoSize = true };
    private readonly ChatTranscriptView _transcript = new();
    private readonly TextBox _inputBox = new();
    private readonly Label _statusLabel = new();
    // Ticks while a reply is pending so the status shows how long the current step has taken.
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 1000 };
    private readonly System.Diagnostics.Stopwatch _activityWatch = new();
    private string? _activityText;

    private ChatSession? _chatSession;
    private string? _sessionCacheKey;
    // The endpoint host the key in the box was loaded for; keys are stored per host.
    private string _apiKeyHost = "";
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _modelsCts;
    private string? _modelsLoadedFor;
    private McpServerHost? _mcpHost;

    public ChatControl()
    {
        // Two settings rows: connection (endpoint, key) and model options. Each row wraps
        // on its own when the window is narrow instead of scattering controls across lines.
        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 8),
        };

        foreach (var box in new Control[] { _endpointBox, _apiKeyBox, _reasoningBox })
            box.Margin = new Padding(4, 6, 12, 4);
        _modelBox.Margin = new Padding(4, 6, 4, 4);
        foreach (var button in new[] { _forgetButton, _newChatButton, _clearButton, _exportButton })
            button.Margin = new Padding(4, 3, 4, 3);
        _loadModelsButton.Margin = new Padding(4, 3, 12, 3);
        _mcpButton.Margin = new Padding(4, 3, 4, 3);
        _rememberCheck.Margin = new Padding(4, 8, 4, 4);

        settingsPanel.Controls.Add(SettingLabel("Endpoint:"));
        settingsPanel.Controls.Add(_endpointBox);
        settingsPanel.Controls.Add(SettingLabel("API key:"));
        settingsPanel.Controls.Add(_apiKeyBox);
        settingsPanel.SetFlowBreak(_apiKeyBox, true);
        settingsPanel.Controls.Add(SettingLabel("Model:"));
        settingsPanel.Controls.Add(_modelBox);
        settingsPanel.Controls.Add(_loadModelsButton);
        _reasoningBox.Items.AddRange(["Default", "Minimal", "Low", "Medium", "High"]);
        _reasoningBox.SelectedIndex = 2;
        settingsPanel.Controls.Add(SettingLabel("Reasoning:"));
        settingsPanel.Controls.Add(_reasoningBox);
        settingsPanel.Controls.Add(_rememberCheck);
        settingsPanel.Controls.Add(_forgetButton);
        settingsPanel.Controls.Add(_mcpButton);

        _transcript.Dock = DockStyle.Fill;
        // Written once the handle exists, so the text picks up the themed background color.
        _transcript.HandleCreated += (_, _) =>
        {
            if (_transcript.TextLength == 0)
                _transcript.AddNote("Enter your endpoint, model and API key above, then ask anything. "
                    + "The agent can inspect this PC via read-only tools (system info, processes, drives, files).");
        };

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(0, 4, 0, 0) };
        _inputBox.Dock = DockStyle.Fill;
        _inputBox.Multiline = true;
        _inputBox.ScrollBars = ScrollBars.None;
        _inputBox.PlaceholderText = "Type a message... (Enter to send, Shift+Enter for newline)";
        _inputBox.KeyDown += OnInputKeyDown;
        // Show the scroll bar only once the text no longer fits.
        _inputBox.TextChanged += (_, _) => UpdateInputScrollBar();
        _inputBox.Resize += (_, _) => UpdateInputScrollBar();
        _inputBox.HandleCreated += (_, _) => ChatTranscriptView.UseDarkScrollBars(_inputBox);
        _sendButton.Dock = DockStyle.Right;
        _sendButton.AsAccent();
        _stopButton.Dock = DockStyle.Right;
        _sendButton.Click += async (_, _) => await SendAsync();
        _stopButton.Click += (_, _) => _cts?.Cancel();
        _newChatButton.Click += (_, _) => NewChat();
        _clearButton.Click += (_, _) => _transcript.ClearTranscript();
        _exportButton.Click += (_, _) => ExportChat();
        _forgetButton.Click += (_, _) => ForgetSettings();
        _loadModelsButton.Click += async (_, _) => await LoadModelsAsync();
        _mcpButton.Click += async (_, _) => await ToggleMcpServerAsync();
        // Opening the list fetches it once per endpoint/key, but only when a key for this
        // endpoint is present; a new host gets its list on button click, not automatically.
        _modelBox.DropDown += async (_, _) =>
        {
            if (_modelsLoadedFor != ModelsSourceKey() && _apiKeyBox.TextLength > 0)
                await LoadModelsAsync();
        };
        // The key follows the endpoint's host: switching hosts swaps or clears the box, so one
        // endpoint's key can never silently be sent to another.
        _endpointBox.TextChanged += (_, _) => OnEndpointChanged();
        bottomPanel.Controls.Add(_inputBox);
        bottomPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottomPanel.Controls.Add(_stopButton);
        bottomPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
        bottomPanel.Controls.Add(_sendButton);

        // Status on the left, conversation actions on the right, between transcript and input.
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 42 };
        var chatActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0),
        };
        chatActions.Controls.Add(_newChatButton);
        chatActions.Controls.Add(_clearButton);
        chatActions.Controls.Add(_exportButton);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Theme.SubtleText;
        _statusLabel.Text = "Not connected.";
        statusBar.Controls.Add(_statusLabel);
        statusBar.Controls.Add(chatActions);

        // RichTextBox has no padding of its own; a surface-colored frame gives the text room.
        var transcriptFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 4, 8), BackColor = Theme.Surface };
        transcriptFrame.Controls.Add(_transcript);
        Controls.Add(transcriptFrame);
        Controls.Add(statusBar);
        Controls.Add(bottomPanel);
        Controls.Add(settingsPanel);

        _activityTimer.Tick += (_, _) => ShowActivity();
        Disposed += (_, _) =>
        {
            _activityTimer.Dispose();
            var host = _mcpHost;
            _mcpHost = null;
            _ = host?.StopAsync();
        };

        LoadPersistedSettings();
    }

    private static Label SettingLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(0, 9, 0, 4) };

    private void UpdateInputScrollBar()
    {
        var lines = _inputBox.GetLineFromCharIndex(_inputBox.TextLength) + 1;
        var wanted = lines * _inputBox.Font.Height > _inputBox.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
        if (_inputBox.ScrollBars != wanted)
            _inputBox.ScrollBars = wanted;
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
        _transcript.ClearTranscript();
        _statusLabel.Text = "New conversation started.";
    }

    private string ModelsSourceKey() => $"{_endpointBox.Text.Trim().TrimEnd('/')}|{_apiKeyBox.Text.Trim()}";

    /// <summary>Fills the model drop-down from the endpoint's /models list. On failure the
    /// list stays as it was and the model name can still be typed by hand.</summary>
    private async Task LoadModelsAsync()
    {
        _modelsCts?.Cancel();
        _modelsCts = new CancellationTokenSource();
        var token = _modelsCts.Token;
        var sourceKey = ModelsSourceKey();

        _loadModelsButton.Enabled = false;
        _statusLabel.Text = "Loading models...";
        try
        {
            var models = await ModelCatalog.ListModelsAsync(_endpointBox.Text, _apiKeyBox.Text, token);
            if (IsDisposed || token.IsCancellationRequested)
                return;

            // Keep whatever the user typed or picked; refilling Items would otherwise clear it.
            var current = _modelBox.Text;
            _modelBox.BeginUpdate();
            _modelBox.Items.Clear();
            _modelBox.Items.AddRange([.. models]);
            _modelBox.EndUpdate();
            _modelBox.Text = current;
            _modelsLoadedFor = sourceKey;

            _statusLabel.Text = models.Count == 0
                ? "The endpoint returned no models. Type the model name by hand."
                : $"Loaded {models.Count} model(s). Pick one or type a name.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer load replaced this one.
        }
        catch (Exception ex)
        {
            if (IsDisposed)
                return;
            // Remember the failure too, so opening the list does not retry on every click.
            _modelsLoadedFor = sourceKey;
            Log.LogWarning(ex, "Could not list models.");
            _statusLabel.Text = ChatSession.IsUnauthorizedError(ex)
                ? "Could not list models: the endpoint rejected the API key (HTTP 401/403). You can still type a model name."
                : $"Could not list models ({ex.Message}). You can still type a model name.";
        }
        finally
        {
            if (!IsDisposed)
                _loadModelsButton.Enabled = true;
        }
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
            // Fires only when the text differs; cover the saved-endpoint-no-change case directly.
            OnEndpointChanged();
        }
        catch (Exception ex)
        {
            // Corrupt settings file: ignore and start fresh.
            Log.LogWarning(ex, "Could not load persisted chat settings; starting fresh.");
        }
    }

    /// <summary>The key in the box follows the endpoint's host: on a host change, that host's
    /// saved key (if any) replaces the text, else the box is emptied — so a saved key for one
    /// endpoint can never be sent to another one.</summary>
    private void OnEndpointChanged()
    {
        var host = EndpointHost();
        if (host == _apiKeyHost)
            return;
        _apiKeyHost = host;
        if (host.Length == 0)
            return;
        var key = CredentialStore.Load(host);
        if (key is not null)
        {
            if (_apiKeyBox.Text != key)
                _apiKeyBox.Text = key;
            _statusLabel.Text = "Loaded the saved API key for this endpoint.";
        }
        else if (_apiKeyBox.TextLength > 0)
        {
            _apiKeyBox.Clear();
            _statusLabel.Text = "The API key box was emptied: keys are stored per endpoint host.";
        }
    }

    private string EndpointHost() =>
        Uri.TryCreate(_endpointBox.Text.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ? uri.Host : "";

    private void SaveSettings()
    {
        try
        {
            ChatSettingsStore.Save(new ChatSettings(
                _endpointBox.Text.Trim(),
                _modelBox.Text.Trim(),
                _reasoningBox.SelectedItem as string));
            var key = _apiKeyBox.Text.Trim();
            var host = EndpointHost();
            if (key.Length > 0 && host.Length > 0)
                CredentialStore.Save(key, host);
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
        CredentialStore.Delete(EndpointHost());
        _apiKeyBox.Clear();
        _chatSession = null;
        _sessionCacheKey = null;
        _statusLabel.Text = "Saved settings forgotten.";
    }

    /// <summary>Starts or stops the local MCP server that exposes the agent and the read-only tools
    /// to other apps on this PC. Uses the settings currently on screen.</summary>
    private async Task ToggleMcpServerAsync()
    {
        if (_mcpHost is not null)
        {
            var host = _mcpHost;
            _mcpHost = null;
            _mcpButton.Enabled = false;
            try
            {
                await host.StopAsync();
                _transcript.AddNote("MCP server stopped.");
                _statusLabel.Text = "MCP server stopped.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Could not stop the MCP server: {ex.Message}";
            }
            finally
            {
                _mcpButton.Enabled = true;
                _mcpButton.Text = "Start MCP server";
            }
            return;
        }

        McpServerHostOptions options;
        try
        {
            options = new McpServerHostOptions(BuildAgentOptions(), ApproveMcpToolCall);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AI Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_rememberCheck.Checked)
            SaveSettings();

        _mcpButton.Enabled = false;
        _statusLabel.Text = "Starting MCP server...";
        try
        {
            _mcpHost = await McpServerHost.StartAsync(options);
            _mcpButton.Text = "Stop MCP server";
            _transcript.AddNote($"MCP server running at {_mcpHost.Url} — it exposes the read-only tools and "
                + "the AI agent; the bearer token was shown in a dialog and is new on every start.");
            _statusLabel.Text = $"MCP server: {_mcpHost.Url}";
            new McpServerDialog(_mcpHost.ClientConfigJson).ShowDialog(this);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Could not start the MCP server.";
            Log.LogError(ex, "Could not start the MCP server.");
            MessageBox.Show($"Could not start the MCP server: {ex.Message}", "AI Chat",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _mcpButton.Enabled = true;
        }
    }

    private void ExportChat()
    {
        if (!_transcript.HasMessages)
        {
            _statusLabel.Text = "Nothing to export.";
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "md",
            FileName = $"chat-{DateTime.Now:yyyyMMdd-HHmmss}.md",
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            var header = $"# WindowsUtils AI Chat export — {DateTime.Now:F}\n\n"
                + $"Model: {_modelBox.Text.Trim()}\n\n";
            File.WriteAllText(dialog.FileName, header + _transcript.ToMarkdown());
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

    /// <summary>The settings as typed on screen, validated; throws with a message to show when incomplete.</summary>
    private ChatAgentOptions BuildAgentOptions()
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
        return new ChatAgentOptions(endpoint, model, key, SelectedEffort());
    }

    private ChatSession EnsureSession()
    {
        var options = BuildAgentOptions();
        var cacheKey = $"{options.Endpoint}|{options.Model}|{options.ApiKey}|{options.Reasoning}";
        if (_chatSession is not null && cacheKey == _sessionCacheKey)
            return _chatSession;

        _chatSession = ChatSession.Create(options);
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
        _transcript.AddUserMessage(prompt);
        _transcript.BeginAssistantMessage();
        SetActivity(new ChatStreamUpdate(ChatActivity.Waiting));
        _activityTimer.Start();

        try
        {
            try
            {
                await StreamAsync(session, prompt, token);
            }
            catch (Exception ex) when (ChatSession.IsReasoningEffortError(ex) && SelectedEffort() != ReasoningEffort.Default)
            {
                // Model rejects reasoning_effort: drop it and retry once instead of failing.
                Log.LogInformation("Reasoning effort rejected by the model; retrying without it.");
                _reasoningBox.SelectedItem = "Default";
                if (_rememberCheck.Checked)
                    SaveSettings();
                session.DisableReasoningEffort();
                _transcript.EndAssistantMessage();
                _transcript.AddNote("The model does not support reasoning_effort, retrying without it.");
                _transcript.BeginAssistantMessage();
                SetActivity(new ChatStreamUpdate(ChatActivity.Waiting));
                await StreamAsync(session, prompt, token);
            }
            _transcript.EndAssistantMessage();
            _statusLabel.Text = "Ready.";
        }
        catch (OperationCanceledException)
        {
            _transcript.EndAssistantMessage();
            _transcript.AddNote("Cancelled.");
            _statusLabel.Text = "Cancelled.";
        }
        catch (TimeoutException ex)
        {
            // The endpoint went silent. The session is still valid (the failed turn is not in
            // its history), so keep it: sending again continues the same conversation.
            Log.LogWarning(ex, "The endpoint went silent (response timeout).");
            _transcript.EndAssistantMessage();
            _transcript.AddError($"No response: {ex.Message}");
            _statusLabel.Text = "No response from the endpoint.";
        }
        catch (Exception ex)
        {
            // A failed turn can leave the session in an unusable state (e.g. a 400
            // mid-run); drop it so the next send starts fresh instead of failing again.
            Log.LogError(ex, "Chat request failed.");
            session.Reset();
            _transcript.EndAssistantMessage();
            if (ChatSession.IsUnauthorizedError(ex))
                _transcript.AddError("Authorization failed: the endpoint rejected the API key (HTTP 401/403). Check that the key is correct and belongs to this endpoint/model.");
            else
                _transcript.AddError($"Error: {ex.GetType().Name}: {ex.Message}");
            _statusLabel.Text = "Error — check endpoint/model/key.";
        }
        finally
        {
            _activityTimer.Stop();
            _activityWatch.Reset();
            _activityText = null;
            _sendButton.Enabled = true;
            _stopButton.Enabled = false;
        }
    }

    private async Task StreamAsync(ChatSession session, string prompt, CancellationToken token)
    {
        await foreach (var update in session.StreamResponseAsync(prompt, ApproveToolCall, token))
        {
            if (update.Text is not null)
                _transcript.AppendAssistantText(update.Text);
            SetActivity(update);
        }
    }

    /// <summary>Asks before a tool reads a file's text or an environment variable. No is the default.</summary>
    private Task<bool> ApproveToolCall(ToolApprovalRequest request, CancellationToken token)
    {
        // Through the activity text, so the ticking timer keeps showing it while the box is open.
        _activityText = $"Waiting for your permission to run {request.ToolName}...";
        _activityWatch.Restart();
        ShowActivity();
        var answer = MessageBox.Show(this, request.Prompt, "AI Chat",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    /// <summary>Same question as <see cref="ApproveToolCall"/>, but a Kestrel thread asks it:
    /// hop to the UI thread first. Without a window there is nobody to ask, so decline.</summary>
    private Task<bool> ApproveMcpToolCall(ToolApprovalRequest request, CancellationToken token)
    {
        if (IsDisposed || !IsHandleCreated)
            return Task.FromResult(false);
        return InvokeRequired ? Invoke(() => ApproveToolCall(request, token)) : ApproveToolCall(request, token);
    }

    /// <summary>Shows what the agent is doing; the elapsed time restarts when the step changes.</summary>
    private void SetActivity(ChatStreamUpdate update)
    {
        var text = update.Activity switch
        {
            ChatActivity.Thinking => "The model is thinking...",
            ChatActivity.RunningTool => $"Running tool {update.ToolName}...",
            ChatActivity.Writing => "Writing...",
            _ => "Waiting for the model...",
        };
        if (text != _activityText)
        {
            _activityText = text;
            _activityWatch.Restart();
        }
        ShowActivity();
    }

    private void ShowActivity()
    {
        var seconds = (int)_activityWatch.Elapsed.TotalSeconds;
        _statusLabel.Text = seconds > 0 ? $"{_activityText} ({seconds} s)" : _activityText;
    }
}

/// <summary>Shown when the MCP server starts: the ready-to-paste client configuration in a
/// selectable, monospaced text box with a copy button — a MessageBox wraps the text and copies
/// it with hard line breaks, so it cannot be pasted cleanly.</summary>
internal sealed class McpServerDialog : Form
{
    private readonly string _snippet;
    private readonly TextBox _configBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        Font = Theme.MonoFont,
    };
    private readonly Button _copyButton = new() { Text = "Copy config", AutoSize = true };
    private readonly Button _closeButton = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _copiedLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.SubtleText,
        AutoEllipsis = true,
    };

    public McpServerDialog(string snippet)
    {
        _snippet = snippet;
        _configBox.Text = snippet;
        Text = "MCP Server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 400);
        Font = Theme.BodyFont;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 12, 14, 10),
        };
        header.Controls.Add(new Label { Text = "MCP server started.", Font = Theme.SemiboldFont, AutoSize = true });
        header.Controls.Add(new Label
        {
            Text = "Add this entry to the mcp.json of an MCP client on this PC (e.g. VS Code). The token "
                + "is new every time the server starts, so update the client after restarting it. The server "
                + "only accepts connections from this PC and stops when the button is clicked again.",
            AutoSize = true,
            MaximumSize = new Size(508, 0),
            ForeColor = Theme.SubtleText,
        });

        // Docking order: the Fill control first, then Top/Bottom panels.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, 6, 12, 12) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _copyButton.Margin = new Padding(4, 3, 4, 3);
        _closeButton.Margin = new Padding(4, 3, 4, 3);
        buttons.Controls.Add(_copyButton);
        buttons.Controls.Add(_closeButton);
        bottom.Controls.Add(_copiedLabel);
        bottom.Controls.Add(buttons);

        Controls.Add(_configBox);
        Controls.Add(bottom);
        Controls.Add(header);

        AcceptButton = _copyButton;
        CancelButton = _closeButton;
        _copyButton.Click += (_, _) => CopyConfig();
        Theme.Apply(this);
        _copyButton.AsAccent();
    }

    private void CopyConfig()
    {
        try
        {
            Clipboard.SetText(_snippet);
            _copiedLabel.Text = "Copied to the clipboard.";
        }
        catch (Exception ex)
        {
            // Someone else holds the clipboard open; Ctrl+C in the box still works.
            _copiedLabel.Text = $"Could not copy ({ex.Message}); select the text and press Ctrl+C instead.";
        }
    }
}
