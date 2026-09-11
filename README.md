# Remote Debugger

A portable Windows tool for controlling and diagnosing another PC on a trusted local network. One executable provides **Donner le contrôle** (visible interactive agent) and **Prendre le contrôle** (GUI and JSON command line controller).

The command path is **Codex → local controller CLI → encrypted connection → remote agent → Windows application**. GUI buttons and the CLI use the same controller implementation and remote operations. No cloud relay or Internet-facing service is involved.

## Get started

Build with .NET SDK 8 on Windows:

```powershell
./scripts/Initialize-Signing.ps1
./scripts/Build.ps1 -Sign
```

For a normal desktop installation, run `artifacts/installer/RemoteDebugger-<version>-Setup.exe`. It installs for the current user without elevation, creates a Start menu shortcut, and includes its own uninstaller. The portable `artifacts/release/RemoteDebugger.exe` remains available when no installation is wanted. Both forms include the .NET runtime. See [the quick start](docs/GETTING_STARTED.md) for pairing and daily use, and [the CLI reference](docs/CLI.md) for Codex automation. The application interface currently uses French labels; the documentation explains them in English.

The agent opens directly to a six-digit pairing code that rotates every five minutes. The controller discovers PCs on launch, accepts the code with Enter, synchronizes the agent to its own signed executable, and opens the live desktop with mouse and keyboard enabled. Connection and live-frame indicators remain visible throughout support. See [the validation record](docs/VALIDATION.md) for completed test evidence; planned cases are listed separately in [the acceptance matrix](docs/ACCEPTANCE_MATRIX.md).

Features include continuous encrypted JPEG desktop streaming (5 fps cap, actual rate shown), fresh screenshots, keyboard/mouse events, UI Automation controls, file browsing, resumable uploads with SHA-256 verification, downloads, application launch/stop/restart and binary identity, timestamped CPU/RAM/process/volume samples, network/services/event diagnostics, bounded commands, real native debugger attachment with breakpoint evidence, minidumps, and intervention history.

The interactive agent uses the logged-in user's session. One initial administrator setup installs a protected local broker for automatic Private-network rules, session-scoped administrator maintenance, and silent signed updates. Subsequent sessions need no repeated elevation prompts. The broker has no network listener; the visible agent remains unelevated. See [installation and publisher enrollment](docs/INSTALLATION.md).

Closing the controller window keeps it in the system tray. **Terminer l’assistance** ends the support session explicitly. The agent prevents automatic sleep while running and exits after ten minutes without the controller, releasing its power request and maintenance access.

## Development

```powershell
./scripts/Test-Unit.ps1
./scripts/Build.ps1 -IncludeLab -Sign
./scripts/Package.ps1
./scripts/Build-Installer.ps1 -Sign
```

See [architecture and security](docs/ARCHITECTURE.md), [CLI contract](docs/CLI.md), and [test evidence](docs/VALIDATION.md). Application binaries and integration test scripts must run through the configured Hyper-V SYSTEM broker, not on the physical development host. Unit tests only exercise pure logic.

Local session logs, diagnostic captures, signing keys, and build outputs are excluded from the repository.
