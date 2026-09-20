using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace WindowsUtils.Utilities;

public class ChatControl : UtilityControl
{
    private const string Instructions =
        "You are a helpful Windows PC assistant running inside the WindowsUtils app. " +
        "Use the provided tools whenever the user asks about this PC (system, processes, drives, files). " +
        "All tools are read-only; never claim to change anything. Keep answers concise.";

    private readonly TextBox _endpointBox = new() { Text = "https://api.openai.com/v1", Width = 280 };
    private readonly TextBox _modelBox = new() { Text = "gpt-4o-mini", Width = 150 };
    private readonly TextBox _apiKeyBox = new() { Width = 200, UseSystemPasswordChar = true, PlaceholderText = "API key" };
    private readonly Button _sendButton = new() { Text = "Send", Width = 90 };
    private readonly Button _stopButton = new() { Text = "Stop", Width = 90, Enabled = false };
    private readonly Button _newChatButton = new() { Text = "New chat", AutoSize = true };
    private readonly Button _clearButton = new() { Text = "Clear", AutoSize = true };
    private readonly Button _forgetButton = new() { Text = "Forget", AutoSize = true };
    private readonly CheckBox _rememberCheck = new() { Text = "Remember", Checked = true, AutoSize = true };
    private readonly RichTextBox _transcript = new();
    private readonly TextBox _inputBox = new();
    private readonly Label _statusLabel = new();

    private AIAgent? _agent;
    private AgentSession? _session;
    private string? _agentCacheKey;
    private CancellationTokenSource? _cts;

    public ChatControl()
    {
        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 76,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4),
        };

        _endpointBox.Margin = new Padding(4, 6, 4, 4);
        _modelBox.Margin = new Padding(4, 6, 4, 4);
        _apiKeyBox.Margin = new Padding(4, 6, 4, 4);
        _newChatButton.Margin = new Padding(4, 5, 4, 4);
        _clearButton.Margin = new Padding(4, 5, 4, 4);
        _forgetButton.Margin = new Padding(4, 5, 4, 4);
        _rememberCheck.Margin = new Padding(4, 8, 4, 4);

        settingsPanel.Controls.Add(new Label { Text = "Endpoint:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_endpointBox);
        settingsPanel.Controls.Add(new Label { Text = "Model:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_modelBox);
        settingsPanel.Controls.Add(new Label { Text = "API key:", AutoSize = true, Margin = new Padding(4, 9, 0, 4) });
        settingsPanel.Controls.Add(_apiKeyBox);
        settingsPanel.Controls.Add(_newChatButton);
        settingsPanel.Controls.Add(_clearButton);
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
        _session = null;
        _transcript.Clear();
        _statusLabel.Text = "New conversation started.";
    }

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsUtils", "chat.json");

    private void LoadPersistedSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(SettingsPath));
                if (json is not null)
                {
                    if (!string.IsNullOrWhiteSpace(json.Endpoint))
                        _endpointBox.Text = json.Endpoint;
                    if (!string.IsNullOrWhiteSpace(json.Model))
                        _modelBox.Text = json.Model;
                }
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
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new ChatSettings(_endpointBox.Text.Trim(), _modelBox.Text.Trim()),
                new JsonSerializerOptions { WriteIndented = true }));
            if (_apiKeyBox.Text.Length > 0)
                CredentialStore.Save(_apiKeyBox.Text);
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
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch
        {
        }
        CredentialStore.Delete();
        _apiKeyBox.Clear();
        _agent = null;
        _session = null;
        _agentCacheKey = null;
        _statusLabel.Text = "Saved settings forgotten.";
    }

    private sealed record ChatSettings(string Endpoint, string Model);

    /// <summary>Minimal Credential Manager wrapper (advapi32): the API key is stored
    /// OS-encrypted per-user instead of cleartext.</summary>
    private static class CredentialStore
    {
        private const string Target = "WindowsUtils_AIChat_ApiKey";
        private const int Generic = 1;
        private const int LocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref Credential credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        public static void Save(string secret)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Flags = 0,
                    Type = Generic,
                    TargetName = Target,
                    CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob,
                    Persist = LocalMachine,
                };
                if (!CredWrite(ref credential, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeCoTaskMem(blob);
                Array.Clear(bytes);
            }
        }

        public static string? Load()
        {
            if (!CredRead(Target, Generic, 0, out var ptr) || ptr == IntPtr.Zero)
                return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(ptr);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                    return null;
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var secret = System.Text.Encoding.Unicode.GetString(bytes);
                Array.Clear(bytes);
                return secret.Length > 0 ? secret : null;
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public static void Delete()
        {
            CredDelete(Target, Generic, 0);
        }
    }

    private AIAgent EnsureAgent()
    {
        var endpoint = _endpointBox.Text.Trim().TrimEnd('/');
        var model = _modelBox.Text.Trim();
        var key = _apiKeyBox.Text;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Enter a model name (e.g. gpt-4o-mini).");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Enter an API key.");

        var cacheKey = $"{endpoint}|{model}|{key}";
        if (_agent is not null && cacheKey == _agentCacheKey)
            return _agent;

        var chatClient = new ChatClient(model, new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = endpointUri });
        AITool[] tools =
        [
            AIFunctionFactory.Create(new Func<string>(GetCurrentTime)),
            AIFunctionFactory.Create(new Func<string>(GetSystemInfo)),
            AIFunctionFactory.Create(new Func<int, string>(GetProcesses)),
            AIFunctionFactory.Create(new Func<string>(GetDrives)),
            AIFunctionFactory.Create(new Func<string, string, int, string>(ListFiles)),
            AIFunctionFactory.Create(new Func<string, string, int, string>(ListFolders)),
            AIFunctionFactory.Create(new Func<string, int, bool, string>(GetLargestFiles)),
            AIFunctionFactory.Create(new Func<string, int, string>(ReadTextFile)),
            AIFunctionFactory.Create(new Func<string, string>(GetEnvironmentVariable)),
            AIFunctionFactory.Create(new Func<string, string, string>(ComputeFileHash)),
            AIFunctionFactory.Create(new Func<string, string, string>(VerifyFileHash)),
        ];
        _agent = chatClient.AsAIAgent(Instructions, "windows-utils-assistant", "Windows PC assistant", tools, null, null, null);
        _agentCacheKey = cacheKey;
        _session = null;
        return _agent;
    }

    private async Task SendAsync()
    {
        var prompt = _inputBox.Text.Trim();
        if (prompt.Length == 0)
            return;

        AIAgent agent;
        try
        {
            agent = EnsureAgent();
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
            _session ??= await agent.CreateSessionAsync(token);
            await foreach (var update in agent.RunStreamingAsync(prompt, _session, cancellationToken: token))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    AppendText(update.Text);
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
            AppendText($"\n(error: {ex.Message})\n");
            _statusLabel.Text = "Error — check endpoint/model/key.";
        }
        finally
        {
            _sendButton.Enabled = true;
            _stopButton.Enabled = false;
        }
    }

    private void AppendText(string text)
    {
        _transcript.AppendText(text);
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.ScrollToCaret();
    }

    [Description("Gets the current date and time on this PC.")]
    private static string GetCurrentTime()
    {
        try
        {
            return DateTime.Now.ToString("F");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets basic system info: machine name, user, OS, CPU count, .NET runtime.")]
    private static string GetSystemInfo()
    {
        try
        {
            return $"Machine: {Environment.MachineName}\n"
                + $"User: {Environment.UserName}\n"
                + $"OS: {Environment.OSVersion} (64-bit OS: {Environment.Is64BitOperatingSystem})\n"
                + $"Processors: {Environment.ProcessorCount}\n"
                + $"Runtime: {Environment.Version}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the top running processes by memory usage.")]
    private static string GetProcesses(
        [Description("Maximum number of processes to return (1-30).")] int maxProcesses = 10)
    {
        try
        {
            maxProcesses = Math.Clamp(maxProcesses, 1, 30);
            var rows = new List<(string Name, int Id, long Mb)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    long mb;
                    try
                    {
                        mb = process.WorkingSet64 / 1024 / 1024;
                    }
                    catch
                    {
                        continue;
                    }
                    rows.Add((process.ProcessName, process.Id, mb));
                }
            }
            return string.Join(Environment.NewLine,
                rows.OrderByDescending(r => r.Mb).Take(maxProcesses).Select(r => $"{r.Name} (Id {r.Id}, {r.Mb} MB)"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists ready drives with free/total space.")]
    private static string GetDrives()
    {
        try
        {
            var rows = DriveInfo.GetDrives().Select(d =>
            {
                if (!d.IsReady)
                    return $"{d.Name} ({d.DriveType}, not ready)";
                return $"{d.Name} ({d.DriveType}, {d.VolumeLabel}) free {d.AvailableFreeSpace / 1024 / 1024 / 1024} GB of {d.TotalSize / 1024 / 1024 / 1024} GB";
            });
            return string.Join(Environment.NewLine, rows);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists files and folders in a directory. Read-only.")]
    private static string ListFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory,
        [Description("Search pattern, e.g. *.log.")] string pattern = "*.*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found: {directory}";
            maxResults = Math.Clamp(maxResults, 1, 100);
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "*.*";
            var entries = Directory.EnumerateFileSystemEntries(resolved, pattern).Take(maxResults).ToArray();
            return entries.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists subfolders in a directory. Read-only.")]
    private static string ListFolders(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory,
        [Description("Search pattern, e.g. Proj*.")] string pattern = "*",
        [Description("Maximum entries to return (1-100).")] int maxResults = 50)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found: {directory}";
            maxResults = Math.Clamp(maxResults, 1, 100);
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = "*";
            var entries = Directory.EnumerateDirectories(resolved, pattern).Take(maxResults).ToArray();
            return entries.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Lists the largest files in a folder, sorted by size descending. Use for 'biggest files' questions.")]
    private static string GetLargestFiles(
        [Description("Directory path (e.g. C:\\Temp) or a shortcut: Documents, Desktop, Downloads.")] string directory = "Documents",
        [Description("How many files to return (1-50).")] int top = 10,
        [Description("Search subfolders too.")] bool recursive = true)
    {
        try
        {
            var resolved = ResolveDirectory(directory);
            if (resolved is null)
                return $"Directory not found: {directory}";
            top = Math.Clamp(top, 1, 50);

            // Min-heap by size: constant memory, same pattern as the Largest Files utility.
            var heap = new PriorityQueue<(string Path, long Size), long>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            };
            foreach (var path in Directory.EnumerateFiles(resolved, "*", options))
            {
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    continue;
                }
                heap.Enqueue((path, size), size);
                if (heap.Count > top)
                    heap.Dequeue();
            }
            if (heap.Count == 0)
                return "(empty)";
            return string.Join(Environment.NewLine, heap.UnorderedItems
                .OrderByDescending(e => e.Priority)
                .Select(e => $"{FormatBytes(e.Element.Size),-10} {e.Element.Path}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string? ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var key = directory.Trim().Trim('"');
        var candidate = key.ToLowerInvariant() switch
        {
            "documents" or "mydocuments" or "my documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "userprofile" or "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => key,
        };
        return Directory.Exists(candidate) ? candidate : null;
    }

    [Description("Reads a text file. Read-only.")]
    private static string ReadTextFile(
        [Description("Full file path.")] string path,
        [Description("Maximum characters to return (max 20000).")] int maxChars = 8000)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return $"File not found: {path}";
            maxChars = Math.Clamp(maxChars, 1, 20000);
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[..maxChars] + "\n...(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Gets the value of an environment variable.")]
    private static string GetEnvironmentVariable(
        [Description("Variable name, e.g. PATH.")] string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
                return "(not set)";
            return value.Length <= 2000 ? value : value[..2000] + "\n...(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Computes the hash of a file. Read-only.")]
    private static string ComputeFileHash(
        [Description("Full file path.")] string path,
        [Description("Algorithm: MD5, SHA-1, SHA-256, or SHA-512.")] string algorithm = "SHA-256")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return $"File not found: {path}";
            using var hashAlgorithm = CreateHashAlgorithm(algorithm, out var name);
            if (hashAlgorithm is null)
                return $"Unknown algorithm '{algorithm}'. Use MD5, SHA-1, SHA-256, or SHA-512.";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            return $"{name}: {Convert.ToHexString(hashAlgorithm.ComputeHash(stream)).ToLowerInvariant()}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Checks whether a file matches an expected hash. Picks the algorithm from the hash length.")]
    private static string VerifyFileHash(
        [Description("Full file path.")] string path,
        [Description("Expected hash in hex (MD5=32 chars, SHA-1=40, SHA-256=64, SHA-512=128).")] string expectedHash)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return $"File not found: {path}";
            var expected = expectedHash.Trim().ToLowerInvariant();
            var algorithm = expected.Length switch
            {
                32 => "MD5",
                40 => "SHA-1",
                64 => "SHA-256",
                128 => "SHA-512",
                _ => null,
            };
            if (algorithm is null)
                return $"Cannot tell the algorithm from a {expected.Length}-character hash.";
            var actual = ComputeFileHash(path, algorithm);
            if (actual.StartsWith("Error", StringComparison.Ordinal) || actual.StartsWith("File not found", StringComparison.Ordinal))
                return actual;
            return actual.EndsWith(expected, StringComparison.Ordinal)
                ? $"MATCH ({algorithm})"
                : $"NO MATCH ({algorithm}). Expected {expected}, got {actual[(algorithm.Length + 2)..]}.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static HashAlgorithm? CreateHashAlgorithm(string algorithm, out string name)
    {
        switch (algorithm.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal))
        {
            case "MD5": name = "MD5"; return MD5.Create();
            case "SHA1": name = "SHA-1"; return SHA1.Create();
            case "SHA256": name = "SHA-256"; return SHA256.Create();
            case "SHA512": name = "SHA-512"; return SHA512.Create();
            default: name = algorithm; return null;
        }
    }
}
