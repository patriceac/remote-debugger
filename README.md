# Remote Debugger

A portable Windows tool for controlling and diagnosing another PC on a trusted local network. One executable provides **Donner le contrôle** (visible interactive agent) and **Prendre le contrôle** (GUI and JSON command line controller).

The command path is **Codex → local controller CLI → encrypted connection → remote agent → Windows application**. GUI buttons and the CLI use the same controller implementation and remote operations. No cloud relay or Internet-facing service is involved.

## Get started

Build with .NET SDK 8 on Windows:

```powershell
./scripts/Build.ps1
```

Copy `artifacts/release/RemoteDebugger.exe` to each Windows x64 PC. The Release includes its .NET runtime; no runtime installation is required. See [the quick start](docs/GETTING_STARTED.md) for pairing and daily use, and [the CLI reference](docs/CLI.md) for Codex automation. The application interface currently uses French labels; the documentation explains them in English.

The 0.1.0 Release passed 34 unit tests and the 44-step application scenario in one disconnected Hyper-V VM. Complete two-PC acceptance, live-view GUI input across two desktops, and actual UAC/elevated maintenance remain unvalidated. See [the validation record](docs/VALIDATION.md) for the exact scope and measured 4.83 fps Full HD stream.

Features include continuous encrypted JPEG desktop streaming (5 fps cap, actual rate shown), fresh screenshots, keyboard/mouse events, UI Automation controls, file browsing, resumable uploads with SHA-256 verification, downloads, application launch/stop/restart and binary identity, timestamped CPU/RAM/process/volume samples, network/services/event diagnostics, bounded commands, real native debugger attachment with breakpoint evidence, minidumps, and intervention history.

The interactive agent uses the logged-in user's session. An optional visible one-hour helper, approved locally through UAC, accepts maintenance commands from the interactive agent; the agent remains the only network listener and no service is installed. A per-operation UAC alternative is also available. Windows sign-in and UAC secure-desktop prompts require local human interaction.

## Development

```powershell
./scripts/Test-Unit.ps1
./scripts/Build.ps1 -IncludeLab
./scripts/Package.ps1
```

See [architecture and security](docs/ARCHITECTURE.md), [CLI contract](docs/CLI.md), and [test evidence](docs/VALIDATION.md). Application binaries and integration test scripts must run through the configured Hyper-V SYSTEM broker, not on the physical development host. Unit tests only exercise pure logic.

Local session logs, diagnostic captures and build outputs are excluded from the repository. The validation record summarizes the previous test run and its remaining limitations.
