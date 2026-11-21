# WindowsUtils — Feature List

A Windows Forms app (.NET 10) that bundles small Windows utilities in one window with
sidebar navigation. Every read-only capability exists in three places at once: the GUI
screens, the AI Chat / MCP tools (`WindowsUtils.AI/PcTools.cs`), and the `wutils` CLI
(`WindowsUtils.Cli`).

> **Tested ✓** — every feature listed here was exercised on 21 Nov 2026 (build, all 34 CLI
> commands, app launch, all 21 screens, the MCP server over real HTTP, and the scoped
> destructive paths): **86/86 checks PASS**. Details and evidence: [TEST-REPORT.md](TEST-REPORT.md).

## Desktop app (WindowsUtils)

### Disk & storage

| Screen | What it does |
|---|---|
| **Folder Size Analyzer** | What takes up space inside a folder: subfolder sizes, recursive scan with cancellation and progress. |
| **Largest Files** | Top-N biggest files on a drive/folder (min-heap, constant memory, junction-safe enumeration). |
| **Duplicate Files** | Identical files grouped by size → prefix hash → SHA-256; delete via Recycle Bin after confirmation. |
| **Temp Cleaner** | Measures temp/cache folders (temp, browser caches, ...), deletes files older than 24 h by open handle (junction-safe), confirmation before deleting. |
| **Disk Info** | Capacity and free space for every drive. |
| **Disk Health** | Physical disk health via Storage WMI (`MSFT_PhysicalDisk`, reliability counters) + SMART `FailurePredictStatus`; counters need admin. |

### System information

| Screen | What it does |
|---|---|
| **System Information** | OS, CPU, memory and .NET runtime at a glance. |
| **Update History** | Windows Update history via `Microsoft.Update.Session` COM: installed, failed and removed updates. |
| **Event Log Summary** | Recent critical/error/warning events from System + Application logs grouped by source (wevtapi, XML rendering). |
| **Installed Programs** | Programs from the Uninstall registry keys (HKLM 64/32-bit + HKCU) with size and install date. |
| **Startup Programs** | Registry Run/RunOnce autostart entries. |
| **Environment Variables** | Browse user, machine and process variables. |
| **Battery and Power** | Battery charge, health, cycle count (WMI) and the active power plan. |
| **Runtime detectors** (inside System Information) | Installed Java (JAVA_HOME/registry/PATH), Python (PATH, PEP 514, py manager, pyenv-win), Node.js (PATH, registry, nvm-windows/Volta/fnm) and .NET Framework versions. |

### Users

| Screen | What it does |
|---|---|
| **Local Users** | Local account list (netapi32): name, full name, status, last logon, password age. **Add User...** wizard and **Delete User** (admin, confirmation, rollback; UI-only — not exposed to AI Chat). |

### Networking

| Screen | What it does |
|---|---|
| **Network Info** | Adapters, addresses, MAC and a quick ping. |
| **TCP Traceroute** | TCP-based traceroute (TTL-stepped SYN probes + ICMP hop mapping, needs admin), host name resolution. |
| **Firewall Ports** | TCP/UDP ports opened by enabled inbound firewall rules (HNetCfg.FwPolicy2 COM, read-only). |
| **Open Ports** | TCP/UDP listeners and connections with owning PID (iphlpapi extended tables, IPv4+IPv6). |
| **TLS Settings** | SSL/TLS versions enabled in Windows Schannel (client + server, registry or OS defaults) **and** a TLS probe of a server: version, cipher suite, certificate chain. |

### Files

| Screen | What it does |
|---|---|
| **File Hash Calculator** | Compute and verify MD5, SHA-1, SHA-256, SHA-384, SHA-512 hashes. |

### AI Chat

| Screen | What it does |
|---|---|
| **AI Chat** | Streaming chat with an agent (Microsoft Agent Framework, any OpenAI-compatible endpoint). Endpoint/model saved to `%AppData%\WindowsUtils\chat.json`; API key stored in Windows Credential Manager (per host). Reasoning-effort injection, retry and endpoint timeout handling. Tool calls that read private data (file contents, environment values) ask Yes/No before running (default No). Transcripts rendered in a chat view. |

### Shell features

- Owner-drawn Windows 11 style sidebar navigation; pages created lazily and cached across tab switches.
- Light/dark theme follows Windows; accent color from the Windows accent color.
- UI language: English or Turkish (`L`/resx lookup), picked from the sidebar; dates/numbers keep the Windows regional format.
- Daily rolling log files under `%AppData%\WindowsUtils\logs`.

## AI Chat tools (WindowsUtils.AI, 34 tools)

All read-only PC inspection. Untrusted model-chosen paths go through `ToLocalPath`/`ResolveDirectory`
(local drives only — no UNC/device/mapped drives). Output is capped; long scans accept a
`CancellationToken`.

**No approval needed:** current time, system info, .NET Framework/Java/Python/Node versions,
optional Windows features, top processes by memory, drives, list files/folders, largest files,
duplicate files, folder sizes, temp sizes, startup programs, battery report, installed programs,
local users, network adapters, firewall ports, open ports, update history, event log summary,
disk health, TLS protocol settings, compute hash, verify hash.

**Approval required (private data, user is asked first):** `ReadTextFile`, `GetEnvironmentVariable`,
`ListEnvironmentVariables`.

**Open-world (network calls):** `PingHost`, `TraceTcpRoute`, `CheckTlsEndpoint`.

### MCP server

- Local MCP server over streamable HTTP at `http://localhost:5199/mcp` (loopback only), started/stopped from the AI Chat screen.
- Per-start bearer token (`McpServerHost.Token`, shown with a ready `mcp.json` snippet).
- Same tool list and the same approval gate as AI Chat; additionally exposes the agent itself as the `ask_windows_utils_agent` tool.
- DNS-rebinding guard: browser requests whose `Host`/`Origin` is not the loopback origin are rejected (403).
- Tools annotated `ReadOnly` + non-`Destructive`; network tools and the agent are `OpenWorld`.
- Requires the ASP.NET Core runtime (comes with the SDK).

## `wutils` CLI (WindowsUtils.Cli, 34 commands)

Console access to the same read-only tools; results to stdout, errors to stderr with exit code 2.
Long scans stop with Ctrl+C.

| Command | Aliases | Usage |
|---|---|---|
| `time` | | Current date and time. |
| `system` | `sysinfo` | OS, CPU, memory and .NET runtime. |
| `netfx` | | Installed .NET Framework version(s). |
| `java` | | Installed Java (JDK/JRE). |
| `python` | | Installed Python. |
| `node` | | Installed Node.js. |
| `features` | | Enabled optional Windows features. |
| `processes` | | Top processes by memory usage: `processes [n]`. |
| `disks` | | Drives with free/total space. |
| `network` | | Network adapters, addresses and MAC. |
| `firewall` | | Enabled inbound firewall ports. |
| `ports` | `netstat` | `ports [--all] [filter] [n]` — open ports with owning process. |
| `updates` | `wu` | `updates [n] [--failed]` — Windows Update history. |
| `events` | `eventlog` | `events [hours] [--errors] [n]` — event log summary. |
| `diskhealth` | `smart` | Physical disk health and SMART counters (counters need admin). |
| `ping` | | `ping <host> [n]`. |
| `traceroute` | `trace` | `traceroute <host> [port] [hops] [--no-names] [--timeout ms]` (needs admin). |
| `tls` | `schannel` | SSL/TLS versions enabled in Windows Schannel. |
| `tlscheck` | `cert` | `tlscheck <host> [port]` — TLS version, cipher and certificate chain. |
| `programs` | `apps` | `programs [name\|size\|date] [n] [filter]`. |
| `startup` | | Programs registered to start with Windows. |
| `battery` | `power` | Battery health, charge and the active power plan. |
| `users` | | Local user accounts and their status. |
| `env` | `getenv` | `env <name>` — value of an environment variable. |
| `envlist` | `envall` | `envlist [User\|Machine\|Process] [n]` — list variables (values may be sensitive). |
| `files` | | `files <dir> [pattern] [n]` — list files and folders. |
| `folders` | | `folders <dir> [pattern] [n]` — list subfolders. |
| `largest` | | `largest <dir> [n]` — largest files, recursive. |
| `foldersize` | `dirsize` | `foldersize <dir> [n]` — what takes space inside a folder. |
| `duplicates` | `dup` | `duplicates <dir> [minMb] [n]` — duplicate files by content. |
| `temp` | `tempsize` | Space used by temp/cache folders (deletes nothing). |
| `read` | | `read <file> [n]` — first n characters of a text file. |
| `hash` | | `hash <file> [algorithm]` — MD5, SHA-1, SHA-256 or SHA-512. |
| `verify` | | `verify <file> <hash>` — check a file against an expected hash. |

## Cross-cutting behavior

- **Read-only by design.** AI tools, MCP tools and the CLI never delete, write or change the
  registry. Destructive actions (duplicate delete, temp clean, user delete) exist only in the
  GUI behind a confirmation; deleted files go to the Recycle Bin (with a permanent-delete
  warning when recycling is impossible).
- **Railway Oriented Programming.** All Core logic returns FluentResults `Result`/`Result<T>`;
  expected failures (access denied, not found, Win32 errors) are messages, not exceptions;
  cancellation propagates as `OperationCanceledException`.
- **Junction-safe file operations.** Enumeration skips inaccessible/reparse entries; bulk deletes
  check the final path on an open handle before deleting through it.
- **arm64-safe P/Invoke.** Native tables are read as bytes at documented offsets; COM/WMI is
  preferred when available.
