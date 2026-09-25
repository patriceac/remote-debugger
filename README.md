# Remote Debugger

A portable Windows tool for controlling and diagnosing another PC on a trusted local network or through a private internet relay. One executable provides **Give control** (visible interactive agent) and **Take control** (GUI and JSON command line controller).

New installations are agents. Becoming a controller requires the setup controller checkbox and the controller password. Agents have **Support enabled** permanently ON and **Take control** disabled. Controllers can initiate support, but receive it only when their **Support enabled** setting is ON; it defaults OFF. This permission includes administrator support. Update controllers to 0.5.23 before updating agents.

The command path is **Codex → local controller CLI → encrypted connection → remote agent → Windows application**. GUI buttons and the CLI use the same controller implementation and remote operations. Optional internet support uses outbound WebSockets on port 443 through a private Cloudflare relay, with the existing endpoint authentication and encryption inside that connection. See [internet setup](docs/INTERNET.md).

Screen sharing uses DXGI capture on supported single displays and tries hardware H.264 encoding before software H.264/JPEG. Rotated, combined, or unsupported displays retain GDI capture. Unchanged desktops back off quickly and remote input wakes capture immediately. The viewer reuses frame buffers, updates statistics four times per second, and retains five-second resource refresh on direct connections. The first resource sample establishes a baseline; utilization appears with the next sample. MCP uses a persistent worker with fresh target/session checks for every operation.

## Get started

For your personal internet-enabled installation, use `RemoteDebugger-<version>-Private-Setup.exe` on each PC. Enter your setup passphrase once at first launch; this Windows account then remembers access. Remote Debugger starts in the system tray after sign-in or an automatic update; shortcuts and the installer's launch button open its window. On your support PC, **Take control** resumes your saved active session when available; otherwise select a computer and click **Connect**. To create a protected setup or remotely migrate existing computers, quit any running Remote Debugger instance (including the tray instance), then launch `RemoteDebugger.exe --security` on the controlling PC. See [security setup and migration](docs/SECURITY_SETUP.md).

Build with .NET SDK 8 on Windows:

```powershell
./scripts/Initialize-Signing.ps1
./scripts/Build.ps1 -Sign
```

For a normal desktop installation, run `artifacts/installer/RemoteDebugger-<version>-Setup.exe`. It requests administrator approval, installs the single application beneath `Program Files\RemoteDebugger`, creates a machine Start menu shortcut, and includes its own uninstaller. It removes any legacy per-user installation beneath `%LOCALAPPDATA%\Programs\Remote Debugger` during setup. The portable `artifacts/release/RemoteDebugger.exe` remains available when no installation is wanted. Both forms include the .NET runtime. See [the quick start](docs/GETTING_STARTED.md) for pairing and daily use, and [the CLI reference](docs/CLI.md) for Codex automation. The app and installer select French, English or Spanish from the Windows display language, with English as the fallback. Use **Language** at the bottom of the sidebar to choose **System default**, **English**, **Français** or **Español**. Changes apply immediately without interrupting support and are saved for future launches. Regional variants such as French Canadian and Mexican Spanish are supported. Numbers and dates keep the user’s Windows regional formats.

Without a private internet profile, LAN mode uses a six-digit pairing code that rotates every five minutes. Both modes synchronize the agent to the controller's signed executable and open the live desktop with mouse and keyboard enabled. Connection and live-frame indicators remain visible throughout support. See [the validation record](docs/VALIDATION.md) for completed test evidence; planned cases are listed separately in [the acceptance matrix](docs/ACCEPTANCE_MATRIX.md).

Features include adaptive encrypted H.264/JPEG desktop streaming (up to 30 fps, unchanged images suppressed), fresh screenshots, keyboard/mouse events, UI Automation controls, file browsing, resumable binary uploads and downloads with progress, cancellation and SHA-256 verification, application launch/stop/restart and binary identity, timestamped CPU/RAM/process/volume samples, network/services/event diagnostics, bounded commands, real native debugger attachment with breakpoint evidence, minidumps, and intervention history.

The support workflow adds connected-session text clipboard sharing, 30 days of automatic local incident reports, confirmed controller restart/shutdown actions, and copy-only file/folder drag-and-drop between Explorer and both the Files pane and remote viewer. Restart preflight explains whether manual Windows sign-in is expected, with an optional one-use sign-in for eligible local accounts and a cancellable one-hour reconnect wait. See [support workflow requirements and acceptance](docs/SUPPORT_WORKFLOW.md) for scope and verification status.

The focused viewer captures Windows shortcuts remotely. **Ctrl+Alt+F12** or **Ctrl+Shift+F12** releases the keyboard and exits full screen; the full-screen bar also keeps an exit button visible. Input and clipboard choices are saved per device. Enter in the text field sends its contents; **Send Enter** presses the remote Enter key. **Ctrl+Alt+Del** uses the installed administrator support service; the protected Windows security screen still requires handling on the remote PC. Direct LAN/WAN sessions refresh processes and CPU/memory/disk/GPU charts every five seconds; economy mode applies only to relay screen streams.

Discovery and connection prefer LAN, then the device's optional configured WAN address, then relay. Select a device to save a hostname or IPv4 address with an optional port (default TCP 45832); leave it blank for LAN → relay. Failed routes have a cooldown and are reconsidered when the network changes. An unchanged desktop reduces capture polling and still responds to a fresh-frame request after a connection interruption.

The interactive agent uses the logged-in user's session. Initial setup automatically requests Windows elevation and installs a protected local broker for Private-network rules, administrator operations, elevated-window input, and silent signed updates. Subsequent authorized sessions need no repeated elevation prompts. The broker has no network listener; the visible agent remains unelevated. See [installation and publisher enrollment](docs/INSTALLATION.md).

Only one desktop instance runs per Windows user session. Launching the app again restores the existing window, including from the system tray, and preserves its workspace and language. The guard applies across portable and installed copies. CLI commands and service helpers can still run alongside the desktop app.

Closing either window keeps the application and connection in the system tray. Restoring the controller resumes its live view automatically. **End support** immediately revokes the current session; an agent remains available with a new invitation or code. Controllers keep their chosen receiving setting. A lost connection has a ten-minute reconnect grace period before that session expires. Use **Quit** in the tray menu to exit immediately.

The available-PC, process and file tables remember their column widths and order separately. Drag a header to reorder it, drag its edge to resize it, or double-click an edge to fit its contents. Preferences survive restarting and use logical widths so changing display scaling preserves the layout.

Saved PCs support **Wake settings** and **Wake** on the Connection page, including
local broadcasts, a configured router destination, or an authenticated awake helper
on a remote LAN. See [Wake-on-LAN](docs/WAKE_ON_LAN.md) for setup and network requirements.

## Development

Codex can use ten typed [local MCP tools](docs/MCP.md), including local incident reports available after disconnection.
The stdio adapter wraps the signed CLI with target binding, bounded requests,
structured errors and image results. Build it with `./scripts/Build-Mcp.ps1`.

```powershell
./scripts/Test-Unit.ps1
./scripts/Build.ps1 -IncludeLab -Sign
./scripts/Package.ps1
./scripts/Build-Installer.ps1 -Sign
```

See [architecture and security](docs/ARCHITECTURE.md), [interface languages](docs/LOCALIZATION.md), [CLI contract](docs/CLI.md), and [test evidence](docs/VALIDATION.md). Application binaries and integration test scripts must run through the configured Hyper-V SYSTEM broker, not on the physical development host. Unit tests exercise logic and isolated control bindings without launching the application.

Local session logs, diagnostic captures, signing keys, and build outputs are excluded from the repository.
