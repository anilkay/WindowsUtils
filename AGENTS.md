# WindowsUtils

A Windows Forms app (.NET 10, `net10.0-windows`) that bundles small Windows utilities in one window with sidebar navigation.

## Build & run

```powershell
dotnet build                        # from repo root (solution dir)
dotnet run --project WindowsUtils   # run the app
```

NuGet packages live in `WindowsUtils.AI` (Microsoft Agent Framework: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI` + `OpenAI` + `Microsoft.Extensions.AI`; MCP server: `ModelContextProtocol.AspNetCore` + the `Microsoft.AspNetCore.App` framework reference). `WindowsUtils.Core` depends only on `Microsoft.Extensions.Logging.Abstractions` (for `ILogger`). The WinForms app references `Microsoft.Extensions.Logging` (for `LoggerFactory`).

## Structure

```
WindowsUtils.slnx
WindowsUtils.Core/       # class library (net10.0, no WinForms): reusable logic
├── Hashing/FileHasher.cs # file hashing (MD5/SHA-1/SHA-256/SHA-384/SHA-512) + verify
├── ByteFormatter.cs      # human-readable byte sizes
├── IO/FileScanner.cs     # safe file scanning: top-N largest files, folder contents, directory sizes
├── IO/DuplicateFinder.cs # duplicate files: group by size, then prefix hash, then SHA-256
├── IO/RecycleBin.cs      # send to Recycle Bin via shell, asks before permanent delete
├── Net/NetworkInfo.cs    # network adapters + ping
├── Net/TcpTraceroute.cs  # TCP traceroute: TTL-stepped SYN probes + ICMP hop mapping (needs admin)
├── Net/FirewallRules.cs  # enabled inbound allow rules with TCP/UDP ports via HNetCfg.FwPolicy2 COM (read-only)
├── Logging/FileLoggerProvider.cs # ILoggerProvider writing to a daily rolling file under %AppData%\WindowsUtils\logs
├── Logging/AppLog.cs     # process-wide logger factory accessor (AppLog.Logger<T>()) + log directory
├── SystemInfo/StartupPrograms.cs # registry Run/RunOnce entries (read-only)
├── SystemInfo/LocalUsers.cs # local user accounts via netapi32 NetUserEnum (read-only)
├── SystemInfo/UserManagement.cs # create/delete local users via netapi32 (needs admin); UI-only, not exposed to AI Chat
├── SystemInfo/RuntimeInstallation.cs # shared record + probe helpers (PATH search, link resolution, version sort) for the runtime detectors
├── SystemInfo/JavaDetector.cs # installed Java: JAVA_HOME, vendor registry keys, PATH; reads the "release" file
├── SystemInfo/PythonDetector.cs # installed Python: PATH (skips WindowsApps aliases), PEP 514 registry keys, Python install manager (%LOCALAPPDATA%\Python), pyenv-win
├── SystemInfo/NodeDetector.cs # installed Node.js: PATH, installer registry key, nvm-windows, Volta, fnm
├── SystemInfo/WindowsFeatures.cs # optional Windows features (Telnet, IIS, SMB1, Hyper-V, WSL, ...) via WMI COM, read-only
└── SystemInfo/NetFrameworkDetector.cs # installed .NET Framework: NDP\v4\Full Release DWORD (4.5-4.8.1) + legacy v3.5/v3.0/v2.0/v1.1 keys
WindowsUtils.AI/         # class library (net10.0, no WinForms): AI chat logic
├── ChatSession.cs        # agent creation, session, streaming, reasoning-effort retry
├── PcTools.cs            # read-only PC inspection tools exposed to the agent
├── ChatSettingsStore.cs  # endpoint/model persistence (%AppData%\WindowsUtils\chat.json)
├── CredentialStore.cs    # API key in Windows Credential Manager (advapi32), one entry per endpoint host
├── McpServerHost.cs      # local MCP server (streamable HTTP, loopback): agent as tool + PcTools as MCP tools
├── ReasoningEffortChatClient.cs # injects reasoning_effort into requests
└── ResponseTimeoutChatClient.cs # fails a model call with TimeoutException when the endpoint goes silent
WindowsUtils/            # WinForms app (net10.0-windows), references Core
├── Program.cs            # entry point, launches MainForm
├── MainForm.cs           # shell: owner-drawn sidebar nav + page header + content panel
├── Theme.cs              # Windows 11 style colors/fonts, light/dark, accent, button/grid styling
└── Utilities/
    ├── UtilityControl.cs # abstract base for all utility screens (shared helpers)
    └── *Control.cs       # one self-contained UserControl per utility
```

Current utilities: System Information, Disk Info, Network Info, TCP Traceroute, Firewall Ports, File Hash Calculator, Environment Variables, Startup Programs, Local Users, Folder Size Analyzer, Largest Files, Duplicate Files, AI Chat. (Process Manager existed but was removed — do not re-add unless asked.)

## Conventions

- **UI is built in code, not Designer files.** Each utility is a single self-contained `.cs` file — no `.Designer.cs`, no `.resx`.
- **Navigation**: `MainForm.Utilities` is a `(string Name, string Icon, string Description, Func<UserControl> Factory)[]` tuple array (`Icon` is a Segoe Fluent Icons glyph such as `"\uE770"`; `Description` is shown under the page title). Controls are created lazily on first navigation, styled with `Theme.Apply`, and cached (state survives tab switches).
- **Theming**: `Theme.cs` holds the Windows 11 style palette (light/dark follows Windows via `Application.SetColorMode(SystemColorMode.System)`, accent from the Windows accent color) and fonts. Use `Theme.*` colors instead of `SystemColors`/named colors; mark a screen's main action button with `.AsAccent()`. Buttons are owner-painted by `Theme.Apply`, grids by `CreateGrid()`.
- **Docking order**: WinForms docks controls in reverse z-order. Add the `Dock=Fill` control **first**, then `Top`/`Bottom` panels, so panels dock correctly.
- **Reusable logic goes to Core**: UI-agnostic code (hashing, formatting, scanning) lives in `WindowsUtils.Core` (`net10.0`, no WinForms references) so console apps/services can reuse it. The WinForms project holds UI only.
- **AI logic goes to WindowsUtils.AI**: agent setup, session/streaming, tools, settings and credential storage live there. `ChatControl` is a thin UI shell that only calls `ChatSession` / `ChatSettingsStore` / `CredentialStore`.
- **Every screen is also an AI tool**: whatever a utility screen can show must also be available to AI Chat as a tool in `PcTools` (registered in `PcTools.CreateTools()`). Tools call the same Core logic as the screen, are read-only (no delete, write or registry changes; destructive actions stay in the UI behind a confirmation), pass model-chosen paths through `ResolveDirectory` / `ToLocalPath`, and cap their output. Long scans take a `CancellationToken` parameter (AIFunctionFactory binds it and hides it from the model).
- **Tools that return private data need the user's permission**: tools that return file contents or environment variable values (`ReadTextFile`, `GetEnvironmentVariable`) are registered as `new ApprovalRequiredAIFunction(...)`. `ChatSession.StreamResponseAsync` stops before running them and asks its `approveToolCall` callback (`ChatControl` shows a Yes/No box, default No, naming the file or variable and the endpoint host); without a callback they are declined. Listing tools (names, sizes) run without asking.
- **MCP server**: `PcTools.CreateTools()` is the single tool list (function + `NeedsApproval`/`OpenWorld` flags) used by both the chat agent and `McpServerHost` (started/stopped by a button on the AI Chat screen). The MCP path keeps the same approval rule via a gate that asks the `approveToolCall` callback (declines without one) and exposes the agent itself as `ask_windows_utils_agent` (`ChatSession.StreamResponseAsync` with one-shot sessions). It binds loopback only (`http://localhost:5199/mcp`, no silent port fallback), requires a per-start bearer token (`McpServerHost.Token`, shown with a ready `mcp.json` snippet — reconfigure clients after each restart), rejects browser requests whose `Host`/`Origin` is not the server's own loopback origin (403, DNS-rebinding guard), uses the streamable HTTP transport, and needs the ASP.NET Core runtime (comes with the SDK; machines with only the Desktop Runtime can't start the app). Tools are annotated `ReadOnly` + non-`Destructive`; the network tools and the agent are `OpenWorld`.

## Adding a new utility

1. Create `Utilities/MyToolControl.cs : UtilityControl`, build UI in the constructor.
2. Add one line to `MainForm.Utilities`: `("My Tool", "\uE90F", "One-line description.", () => new MyToolControl()),`
3. Expose the same capability to AI Chat: add a read-only `[Description]` method to `PcTools` and one line in `PcTools.CreateTools()` (set `NeedsApproval` if it returns file contents, secrets or similar private data). Chat and the MCP server both pick it up automatically.

## Gotchas (learned the hard way)

- **DataGridView + deferred binding**: column auto-generation only happens once the control is parented to a form (gets a `BindingContext`). Since utility controls are constructed *before* being added to the content panel, never read `grid.Columns["X"]` in a constructor with auto-generated columns — it returns null (NRE). Instead set `AutoGenerateColumns = false` and add columns explicitly via `AddBoundColumn(...)` (sets `DataPropertyName`).
- **Unbound grids** (rows added manually) throw on header-click sorting — call `MakeUnsortable(grid)` after adding columns.
- **AIFunctionFactory marshals results to `JsonElement` by default** (Microsoft.Extensions.AI 10.x). Over MCP the SDK then serializes that JsonElement a second time, so text arrives quoted with `\n` escapes (`"line1\nline2"`) instead of plain text. Create agent/MCP functions via `PcTools.CreateFunction(...)`, which passes results through unchanged.
- **Shared helpers**: UI grid helpers live in `UtilityControl` (`CreateGrid()`, `AddBoundColumn()`, `MakeUnsortable()`); reusable logic lives in Core (`ByteFormatter`, `FileHasher`, `FileScanner`). `UtilityControl.FormatBytes()` just delegates to Core.

## Patterns in use

- Long-running work (scanning, hashing, ping): hashing/scanning implementations live in Core with `CancellationToken` support; UI wraps them in `Task.Run` + `CancellationTokenSource` (cancel previous run before starting a new one) and reports via `Progress<T>`. Never block the UI thread.
- File enumeration: `EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System | ReparsePoint }` — avoids access-denied crashes, junction loops, and OneDrive placeholder downloads.
- Top-N file selection: `PriorityQueue<T, long>` min-heap capped at N (constant memory).
- File deletion: `RecycleBin.SendToRecycleBin(path, owner)` (Core) after a Yes/No confirmation. It passes `FOF_WANTNUKEWARNING`, so files that cannot be recycled (too large, network/USB drive) prompt before permanent deletion; it returns false if the user keeps the file. Do **not** use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`: it silently deletes such files permanently.
- AI tool paths are untrusted (the model picks them): pass them through `PcTools.ToLocalPath` / `ResolveDirectory` before any file-system call. Only paths on known-local drives are allowed: UNC and device paths would make Windows send NTLM credentials to arbitrary hosts, and mapped network drives are rejected by the root's drive type (a subst drive reports itself as Fixed and cannot be told apart).
- Registry access: read-only, wrapped in try/catch (access denied is expected).
- Open file in Explorer: `Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{path}\"")` — always the full path, so a planted `explorer.exe` next to the app's exe cannot be started instead.

## Testing

No test project. Verify with `dotnet build` (must be 0 warnings/0 errors) and a manual launch of the app, navigating to the changed screen.
