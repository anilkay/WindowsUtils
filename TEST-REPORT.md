# WindowsUtils — Test Report

**Date:** 21 Nov 2026 · **Commit:** `409e0ac` · **Machine:** Windows Server VM (QEMU, 4 vCPU, 24 GB), user `Administrator`, .NET 10 SDK.

Every feature in [FEATURES.md](FEATURES.md) was exercised. Headless test harnesses
(scripts + captured outputs) live in `%TEMP%\2\opencode\wutils-tests\`.

## Summary

| Area | Checks | Result |
|---|---|---|
| Build (4 projects) | warnings / errors | **PASS** — 0 warnings, 0 errors |
| Localization resources | Turkish translation coverage | **PASS** — 396/396 texts translated |
| `wutils` CLI | 36 invocations (34 commands + help + 2 ping variants) | **PASS** — 36/36, all exit 0, output content verified |
| WinForms app launch | process stays alive, log file created | **PASS** |
| GUI screens | 21 utility screens + Add User wizard + Turkish string lookup | **PASS** — 23/23 |
| MCP server | start, token, guards, tools, approval gate, port conflict | **PASS** — 17/17 |
| UI-only destructive paths | recycle bin, user add/delete, temp clean | **PASS** — 7/7 (scoped, see below) |

## 1. Build

`dotnet build` at the repo root: **0 warnings, 0 errors** (Debug, all 4 projects).

## 2. `wutils` CLI — 34 commands

All 34 commands ran to completion (exit 0); output was inspected for real data, not just
the exit code. Environment notes: no battery (VM), QEMU virtual disk (no SMART reliability
counters), virtual network (no ICMP time-exceeded replies).

| Command | Status | Evidence |
|---|---|---|
| `time` | PASS | `21 Kasım 2026 Cumartesi 22:45:56` |
| `system` | PASS | `WIN-NSJPVFO9HRH`, Windows NT 10.0.26100, QEMU CPU, 24,39 GB RAM |
| `netfx` | PASS | .NET Framework 4.8.1 (4.8.09221) via registry NDP key |
| `java` | PASS | JDK 21.0.12.1 (Eclipse Adoptium) via JAVA_HOME |
| `python` | PASS | Python 3.12.10 via PATH + 3.14.4 via install manager |
| `node` | PASS | Node 24.13.0 via PATH |
| `features` | PASS | Telnet/IIS/SMB1/Hyper-V/WSL states listed |
| `processes` | PASS | Top processes with memory: claude, sqlservr, opencode-cli… |
| `disks` | PASS | `C:\` 16,48 GB free of 199,03 GB (8,3%); `D:\` CDRom |
| `network` | PASS | Adapter, IPv4/IPv6, MAC; loopback + filters listed |
| `firewall` | PASS | Enabled inbound rules with ports via FwPolicy2 COM |
| `ports` | PASS | 77 listeners/UDP endpoints with owning PIDs (SQL 1433, RDP 3389, PostgreSQL 5432, ollama 11434…) |
| `updates` | PASS | Real history via Microsoft.Update.Session (Defender KB2267602…) |
| `events` | PASS | Real grouped errors/warnings (PostgreSQL, .NET Runtime) |
| `diskhealth` | PASS | Disk 0 Healthy, SMART failure predicted: no (virtual disk reports no counters — expected) |
| `ping` (loopback) | PASS | 2/2 replies, 0 ms |
| `ping` (internet) | PASS | 2/2 replies from 8.8.8.8, avg 11 ms |
| `traceroute` | PASS | Ran 5 TCP hops, correct output format; this virtual network returns no ICMP time-exceeded, so all hops show `*` (environment, not the tool) |
| `tls` | PASS | SSL 3.0 disabled … TLS 1.3 enabled, client+server Schannel defaults |
| `tlscheck` | PASS | www.microsoft.com:443 → TLS 1.3, cipher, full certificate chain OK |
| `programs` | PASS | Installed programs, name-sorted, 10 shown |
| `startup` | PASS | Real Run entries (SecurityHealth, Everything…) |
| `battery` | PASS | AC power, Balanced plan, "none detected" handled correctly |
| `users` | PASS | Administrator/DefaultAccount/Guest/WDAGUtilityAccount with status and last logon |
| `env` | PASS | PATH value printed |
| `envlist` | PASS | Process variables listed |
| `files` | PASS | Repo `.md` files listed |
| `folders` | PASS | Repo subfolders listed |
| `largest` | PASS | Top 5 files with sizes |
| `foldersize` | PASS | Folder sizes, largest 10 shown |
| `duplicates` | PASS | Found 2 real duplicate groups (OpenAI.dll, ModelContextProtocol.Core.dll) |
| `temp` | PASS | 899,65 MB measured across 5 locations, deletes nothing |
| `read` | PASS | First 200 chars of FEATURES.md |
| `hash` | PASS | SHA-256 of FEATURES.md |
| `verify` | PASS | `MATCH (SHA-256)` against the hash just computed |
| `help` | PASS | Command table printed |

Note: mojibake in the captured `out\*.txt` files (`Kas─▒m`) is an artifact of the PowerShell
capture script's code page, not of the CLI — direct runs print cleanly (the CLI forces UTF-8).

## 3. WinForms app launch

Started `WindowsUtils.exe`; the process stayed alive for the full 12 s observation window
(no startup crash: `ApplicationConfiguration.Initialize`, language settings, theme, MainForm
shell with sidebar). The daily rolling log `%AppData%\WindowsUtils\logs\windowsutils-20261121.log`
was created by `FileLoggerProvider`.

## 4. GUI screens — 23/23 PASS

A WinForms harness mirrors the app's startup steps, then constructs **every** utility screen
(the same control types `MainForm` navigates to), shows it on a form for ~1.5 s, pumps
messages, and closes it. A watchdog thread would record (and close) any modal dialog;
none appeared. Screens that load data in their constructor (Disk Info, Startup Programs,
Installed Programs, Local Users, …) ran their real data paths.

All 21 screens PASS: Battery, Chat, Disk Health, Disk Info, Duplicate Files, Environment
Variables, Event Log, File Hash, Firewall Ports, Folder Size, Installed Programs, Largest
Files, Local Users, Network Info, Open Ports, Startup Programs, System Info, TCP Traceroute,
Temp Cleaner, TLS Settings, Update History. Plus:

- `AddUserWizardForm` constructs safely and creates no account just by existing.
- **Turkish localization:** with `tr-TR` UI culture the wizard title loads from
  `Strings.tr.resx` as `Yerel Kullanıcı Ekle` (≠ the English key → the resource lookup works),
  and `tools/Check-Translations.ps1` reports **396/396** texts translated.

Harness note: the first run failed 18/21 because the harness itself skipped
`Theme.Initialize()` (grids rejected the default transparent color). Mirroring the app's
real startup fixed it — a test-harness bug, not an app bug; no product change needed.

## 5. MCP server — 17/17 PASS

`McpServerHost.StartAsync` was driven directly over real HTTP (streamable JSON-RPC):

| Check | Result |
|---|---|
| Fixed loopback URL `http://localhost:5199/mcp` | PASS |
| Fresh 64-hex bearer token per start; ready `mcp.json` snippet | PASS |
| Missing token → **401** | PASS |
| Foreign `Host` header → **403** (DNS-rebinding guard) | PASS |
| Foreign `Origin` header → **403** | PASS |
| Own `Host` accepted → 200 | PASS |
| `initialize` → 200, serverInfo `windowsutils` | PASS |
| `tools/list` → **35 tools** (34 PcTools + `ask_windows_utils_agent`) | PASS |
| Agent tool exposed; ReadOnly annotations present | PASS |
| Tool call `GetCurrentTime` over MCP returns text | PASS |
| `ReadTextFile` with no approval callback → declined ("The user did not allow this tool call.") | PASS |
| `ask_windows_utils_agent` without a real endpoint → graceful `Error:` result (no crash, no hang) | PASS |
| `ReadTextFile` with an approving callback → file content returned | PASS |
| Busy port 5199 → refuses to start with a clear message, no silent port fallback | PASS |

## 6. UI-only destructive paths — 7/7 PASS (scoped)

These features have no CLI/AI path by design, so the same Core code the screens call was
exercised headlessly, with the blast radius minimized:

| Check | Result |
|---|---|
| Recycle bin: a scratch file created by the test is recycled and gone from disk | PASS |
| Users: `wutils-test` account created via netapi32 | PASS |
| Users: account appears in `LocalUsers.GetAccounts()` | PASS |
| Users: duplicate add rejected — "A user named 'wutils-test' already exists." | PASS |
| Users: account deleted, gone from the account list | PASS |
| Temp clean (Windows Temp only): 2 junk files / 155 B deleted, cleanable now 0 | PASS |
| Temp clean (Error Reports only): 2 junk files / 16 934 B deleted, cleanable now 0; second run idempotent | PASS |

User Temp, Edge cache and the other large locations were deliberately left untouched.
The temp-clean run deletes only files older than 24 h through the junction-safe by-handle
path, exactly as the GUI does after confirmation.

Password-policy note: the first `AddUser` attempt was rejected with Windows error 0x8C5
because the test password contained the account name ("Wutils…") — the default complexity
filter forbids 3+ consecutive characters of the account name. Expected Windows behavior;
retested with a compliant password.

## Not exercised (and why)

- **AI Chat conversation with a live model** — no AI endpoint/API key is configured on this
  machine. Everything around it ran headlessly: `ChatSession` creation and the streaming
  pipeline (via the MCP harness, including the graceful-failure path), the tool approval gate
  in both directions (declined / approved), reasoning-effort fallback wiring, and the
  `ChatControl` UI itself in the screen smoke.
- **RecycleBin permanent-delete warning** — requires a file that cannot be recycled (e.g. on
  removable media); no such device present.
- **Manual interactive flows** (clicking Send in AI Chat, the Yes/No approval boxes, the
  language restart prompt) — need a human at the GUI; their underlying logic is covered above.
