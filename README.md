# WindowsUtils

A Windows Forms app (.NET 10) that bundles small, handy Windows utilities in a single window with sidebar navigation. No external dependencies — BCL only.

## Utilities

| Utility | What it does |
| --- | --- |
| **System Information** | OS, CPU, RAM usage, uptime, screen resolution |
| **Disk Info** | All drives with total/free space; highlights drives under 10% free |
| **Network Info** | Adapters, IPs, MAC addresses + built-in ping tool |
| **File Hash Calculator** | MD5 / SHA-1 / SHA-256 / SHA-512 with hash verification |
| **Environment Variables** | Browse User / Machine / Process variables |
| **Startup Programs** | Programs registered to start with Windows (registry Run keys) |
| **Folder Size Analyzer** | Sizes of everything inside a folder, sorted largest first |
| **Largest Files** | Scans drives/folders for the top 100 largest files; open in Explorer or delete to Recycle Bin |

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source

## Build & run

```powershell
dotnet build
dotnet run --project WindowsUtils
```

## Single-file build

Produces one self-contained `WindowsUtils.exe` (no .NET install needed on the target machine):

```powershell
dotnet publish WindowsUtils -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
```

Output: `WindowsUtils\bin\Release\net10.0-windows\win-x64\publish\WindowsUtils.exe`

## Project structure

```
WindowsUtils/
├── Program.cs            # entry point
├── MainForm.cs           # shell: sidebar navigation + content host
└── Utilities/
    ├── UtilityControl.cs # base class with shared grid/format helpers
    └── *Control.cs       # one self-contained UserControl per utility
```

Adding a new utility = one new file in `Utilities/` + one line in `MainForm.Utilities`. See [AGENTS.md](AGENTS.md) for conventions and gotchas.
