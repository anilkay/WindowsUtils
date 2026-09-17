# WindowsUtils

A Windows Forms app (.NET 10, `net10.0-windows`) that bundles small Windows utilities in one window with sidebar navigation.

## Build & run

```powershell
dotnet build                        # from repo root (solution dir)
dotnet run --project WindowsUtils   # run the app
```

No external NuGet packages — BCL only (incl. `Microsoft.VisualBasic.FileIO`, which ships with the runtime).

## Structure

```
WindowsUtils.sln
WindowsUtils/
├── Program.cs            # entry point, launches MainForm
├── MainForm.cs           # shell: SplitContainer with nav ListBox + content panel
└── Utilities/
    ├── UtilityControl.cs # abstract base for all utility screens (shared helpers)
    └── *Control.cs       # one self-contained UserControl per utility
```

Current utilities: System Information, Disk Info, Network Info, File Hash Calculator, Environment Variables, Startup Programs, Folder Size Analyzer, Largest Files. (Process Manager existed but was removed — do not re-add unless asked.)

## Conventions

- **UI is built in code, not Designer files.** Each utility is a single self-contained `.cs` file — no `.Designer.cs`, no `.resx`.
- **Navigation**: `MainForm.Utilities` is a `(string Name, Func<UserControl> Factory)[]` tuple array. Controls are created lazily on first navigation and cached (state survives tab switches).
- **Docking order**: WinForms docks controls in reverse z-order. Add the `Dock=Fill` control **first**, then `Top`/`Bottom` panels, so panels dock correctly.

## Adding a new utility

1. Create `Utilities/MyToolControl.cs : UtilityControl`, build UI in the constructor.
2. Add one line to `MainForm.Utilities`: `("My Tool", () => new MyToolControl()),`

## Gotchas (learned the hard way)

- **DataGridView + deferred binding**: column auto-generation only happens once the control is parented to a form (gets a `BindingContext`). Since utility controls are constructed *before* being added to the content panel, never read `grid.Columns["X"]` in a constructor with auto-generated columns — it returns null (NRE). Instead set `AutoGenerateColumns = false` and add columns explicitly via `AddBoundColumn(...)` (sets `DataPropertyName`).
- **Unbound grids** (rows added manually) throw on header-click sorting — call `MakeUnsortable(grid)` after adding columns.
- **Shared helpers** live in `UtilityControl`: `CreateGrid()`, `AddBoundColumn()`, `MakeUnsortable()`, `FormatBytes()`.

## Patterns in use

- Long-running work (scanning, hashing, ping): `Task.Run` + `CancellationTokenSource` (cancel previous run before starting a new one), `Progress<string>` for status text. Never block the UI thread.
- File enumeration: `EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System | ReparsePoint }` — avoids access-denied crashes, junction loops, and OneDrive placeholder downloads.
- Top-N file selection: `PriorityQueue<T, long>` min-heap capped at N (constant memory).
- File deletion: `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)` — always Recycle Bin, with a Yes/No confirmation first.
- Registry access: read-only, wrapped in try/catch (access denied is expected).
- Open file in Explorer: `Process.Start("explorer.exe", $"/select,\"{path}\"")`.

## Testing

No test project. Verify with `dotnet build` (must be 0 warnings/0 errors) and a manual launch of the app, navigating to the changed screen.
