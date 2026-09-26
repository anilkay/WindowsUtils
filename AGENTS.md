# WindowsUtils

A Windows Forms app (.NET 10, `net10.0-windows`) that bundles small Windows utilities in one window with sidebar navigation.

## Build & run

```powershell
dotnet build                        # from repo root (solution dir)
dotnet run --project WindowsUtils   # run the app
```

NuGet packages live in `WindowsUtils.AI` (Microsoft Agent Framework: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI` + `OpenAI` + `Microsoft.Extensions.AI`). `WindowsUtils.Core` is dependency-free. The WinForms app itself has no package references.

## Structure

```
WindowsUtils.slnx
WindowsUtils.Core/       # class library (net10.0, no WinForms): reusable logic
├── Hashing/FileHasher.cs # file hashing (MD5/SHA-1/SHA-256/SHA-384/SHA-512) + verify
├── ByteFormatter.cs      # human-readable byte sizes
├── IO/FileScanner.cs     # safe file scanning: top-N largest files, directory sizes
├── IO/DuplicateFinder.cs # duplicate files: group by size, then prefix hash, then SHA-256
└── IO/RecycleBin.cs      # send to Recycle Bin via shell, asks before permanent delete
WindowsUtils.AI/         # class library (net10.0, no WinForms): AI chat logic
├── ChatSession.cs        # agent creation, session, streaming, reasoning-effort retry
├── PcTools.cs            # read-only PC inspection tools exposed to the agent
├── ChatSettingsStore.cs  # endpoint/model persistence (%AppData%\WindowsUtils\chat.json)
├── CredentialStore.cs    # API key in Windows Credential Manager (advapi32)
└── ReasoningEffortChatClient.cs # injects reasoning_effort into requests
WindowsUtils/            # WinForms app (net10.0-windows), references Core
├── Program.cs            # entry point, launches MainForm
├── MainForm.cs           # shell: owner-drawn sidebar nav + page header + content panel
├── Theme.cs              # Windows 11 style colors/fonts, light/dark, accent, button/grid styling
└── Utilities/
    ├── UtilityControl.cs # abstract base for all utility screens (shared helpers)
    └── *Control.cs       # one self-contained UserControl per utility
```

Current utilities: System Information, Disk Info, Network Info, File Hash Calculator, Environment Variables, Startup Programs, Folder Size Analyzer, Largest Files, Duplicate Files, AI Chat. (Process Manager existed but was removed — do not re-add unless asked.)

## Conventions

- **UI is built in code, not Designer files.** Each utility is a single self-contained `.cs` file — no `.Designer.cs`, no `.resx`.
- **Navigation**: `MainForm.Utilities` is a `(string Name, string Icon, string Description, Func<UserControl> Factory)[]` tuple array (`Icon` is a Segoe Fluent Icons glyph such as `"\uE770"`; `Description` is shown under the page title). Controls are created lazily on first navigation, styled with `Theme.Apply`, and cached (state survives tab switches).
- **Theming**: `Theme.cs` holds the Windows 11 style palette (light/dark follows Windows via `Application.SetColorMode(SystemColorMode.System)`, accent from the Windows accent color) and fonts. Use `Theme.*` colors instead of `SystemColors`/named colors; mark a screen's main action button with `.AsAccent()`. Buttons are owner-painted by `Theme.Apply`, grids by `CreateGrid()`.
- **Docking order**: WinForms docks controls in reverse z-order. Add the `Dock=Fill` control **first**, then `Top`/`Bottom` panels, so panels dock correctly.
- **Reusable logic goes to Core**: UI-agnostic code (hashing, formatting, scanning) lives in `WindowsUtils.Core` (`net10.0`, no WinForms references) so console apps/services can reuse it. The WinForms project holds UI only.
- **AI logic goes to WindowsUtils.AI**: agent setup, session/streaming, tools, settings and credential storage live there. `ChatControl` is a thin UI shell that only calls `ChatSession` / `ChatSettingsStore` / `CredentialStore`.

## Adding a new utility

1. Create `Utilities/MyToolControl.cs : UtilityControl`, build UI in the constructor.
2. Add one line to `MainForm.Utilities`: `("My Tool", "\uE90F", "One-line description.", () => new MyToolControl()),`

## Gotchas (learned the hard way)

- **DataGridView + deferred binding**: column auto-generation only happens once the control is parented to a form (gets a `BindingContext`). Since utility controls are constructed *before* being added to the content panel, never read `grid.Columns["X"]` in a constructor with auto-generated columns — it returns null (NRE). Instead set `AutoGenerateColumns = false` and add columns explicitly via `AddBoundColumn(...)` (sets `DataPropertyName`).
- **Unbound grids** (rows added manually) throw on header-click sorting — call `MakeUnsortable(grid)` after adding columns.
- **Shared helpers**: UI grid helpers live in `UtilityControl` (`CreateGrid()`, `AddBoundColumn()`, `MakeUnsortable()`); reusable logic lives in Core (`ByteFormatter`, `FileHasher`, `FileScanner`). `UtilityControl.FormatBytes()` just delegates to Core.

## Patterns in use

- Long-running work (scanning, hashing, ping): hashing/scanning implementations live in Core with `CancellationToken` support; UI wraps them in `Task.Run` + `CancellationTokenSource` (cancel previous run before starting a new one) and reports via `Progress<T>`. Never block the UI thread.
- File enumeration: `EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System | ReparsePoint }` — avoids access-denied crashes, junction loops, and OneDrive placeholder downloads.
- Top-N file selection: `PriorityQueue<T, long>` min-heap capped at N (constant memory).
- File deletion: `RecycleBin.SendToRecycleBin(path, owner)` (Core) after a Yes/No confirmation. It passes `FOF_WANTNUKEWARNING`, so files that cannot be recycled (too large, network/USB drive) prompt before permanent deletion; it returns false if the user keeps the file. Do **not** use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`: it silently deletes such files permanently.
- AI tool paths are untrusted (the model picks them): pass them through `PcTools.ToLocalPath` / `ResolveDirectory` before any file-system call. Only drive-letter paths are allowed; UNC and device paths would make Windows send NTLM credentials to arbitrary hosts.
- Registry access: read-only, wrapped in try/catch (access denied is expected).
- Open file in Explorer: `Process.Start("explorer.exe", $"/select,\"{path}\"")`.

## Testing

No test project. Verify with `dotnet build` (must be 0 warnings/0 errors) and a manual launch of the app, navigating to the changed screen.
