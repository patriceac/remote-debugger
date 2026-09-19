# Installation, signing, and updates

## Desktop installer

`RemoteDebugger-<version>-Setup.exe` is a machine-wide installer. It requests
administrator approval and writes the single application beneath
`%ProgramFiles%\RemoteDebugger`, creates a machine Start menu shortcut, and
registers a machine uninstaller. It removes the legacy per-user installation
from `%LOCALAPPDATA%\Programs\Remote Debugger` if one is present. It also
creates a shortcut in the initiating user's Windows Startup folder, so the app
starts in the system tray when that user signs in after boot. Use the tray icon
to open its window; private support still waits for **Enable support**. The Start
menu shortcut opens the window normally. The installed app launches at medium
integrity after the administrator-owned setup completes. The portable single
executable remains supported and behaves the same way.

The installation directory is fixed; unsupported `/DIR` overrides are rejected
before files are installed. User migration, profile import and startup integration
run as the initiating user even when another administrator supplies credentials.
`build-info.json` records the source commit, whether local changes were present,
and the signed application SHA-256.

The personal `RemoteDebugger-<version>-Private-Setup.exe` also embeds the private
encrypted internet setup file. Installation stages ciphertext for a one-time
passphrase unlock in the app, then deletes the temporary extraction. Silent
installation also waits for unlock at the first normal launch. Reinstalling
the same unlocked profile preserves remembered access. The passphrase is never
an installer argument. See [security setup](SECURITY_SETUP.md).

The desktop installer owns the Program Files application. Choosing **Enable
support** still requests one explicit Windows administrator approval so the
application can provision the local broker and Private/LocalSubnet firewall
rules. There is no second installed application to redirect to. Re-running the
installer closes the running agent, removes any legacy per-user package, updates
the Program Files application, and refreshes the protected broker when one is
already provisioned. Remote signed updates through the broker remain silent
after the initial setup.

Build the installer after producing a signed Release:

```powershell
./scripts/Build-Installer.ps1 -Sign
```

The default private build reads `%LOCALAPPDATA%\RemoteDebugger\RemoteDebugger-Protected.rdrelay`,
created by the app's Security window. Use `-InternetProfilePath PATH` for another
encrypted profile, or `-WithoutInternetProfile` to build the distributable
installer without credentials. A missing, invalid or plaintext requested
profile fails the build.
The executable and publisher signing key are unchanged by profile embedding.
Run `scripts/Test-InternetInstaller.ps1` after publishing the acceptance Lab to
verify automatic configuration and first-launch relay registration in Hyper-V.

Inno Setup must be installed or its compiler path supplied with
`-CompilerPath`. Uninstalling the machine package removes its application files
and machine Start menu shortcut, registered user's Startup shortcut, support
service, firewall rules and protected service state. Personal setup and diagnostic
data are preserved. Uninstall refuses to interrupt an active executable replacement
or its health verification; finish or cancel that update first.

The 0.4.13 broker adds a signed input helper in the authorized interactive session
for elevated windows such as Task Manager. It runs only while administrator
maintenance is enabled and an input connection is active. Secure Windows desktops
remain unavailable and report an input permission error. An agent-only update
does not replace a pre-0.4.13 broker: run the current installer once on those agents
to enable the new capability. Normal desktop input remains available meanwhile.

Remote Debugger runs the visible support agent in the signed-in user's desktop.
Silent administrator maintenance and protected executable replacement use a
local privileged broker that must be provisioned once with Windows administrator
approval (or deployed by an administrator). A fresh unelevated portable copy
cannot grant itself administrator rights without that prior setup.

## Publisher identity

The release executable is Authenticode signed. The broker enrolls the publisher
identity during the initial authorized setup and checks it again before accepting
replacement binaries. The distributed `.cer` contains a public certificate only.
It is not a password, private key, or a general grant of access to a computer.

The repository includes tooling for a local publisher identity. This certificate
is not issued by a public certificate authority. Initial Windows approval and
publisher enrollment remain necessary on each receiving PC. No build script
installs a trust root on the development computer.

```powershell
./scripts/Initialize-Signing.ps1
./scripts/Build.ps1 -IncludeLab -Sign
./scripts/Package.ps1
```

The private PFX and its DPAPI-protected password remain beneath
`%LOCALAPPDATA%\RemoteDebugger-build\signing`, restricted to the current Windows
account. They are never included in the package or committed to Git. Back up that
identity securely if continued releases should be accepted by already-enrolled
PCs. The initialization script refuses to replace an incomplete existing identity
automatically. Losing or changing the signing identity requires explicit publisher
re-enrollment on receiving PCs.

## Session and update order

The controlling PC is authoritative for the application binary. Pairing establishes
an authenticated relationship before any update transfer. Each connection uses the
SHA-256 of the executable actually running on the controller, not just a displayed
version label. Live screen/input and normal support operations require matching
agent and controller executable hashes.

If those hashes differ, the controller transfers its signed executable over one
authenticated resumable channel, acknowledging each 512 KiB chunk. The agent
stages and verifies it, and the broker performs a recoverable replacement. Both
upgrades and downgrades are intentional: the controller determines the required
version. A different build with the same version label also requires synchronization.

A planned update creates a short-lived, protected reconnect grant tied to the
paired session and the permitted executable identities. That grant allows the
restarted agent to resume without another code. Ordinary launches require new
pairing. Successful update confirmation removes the temporary grant. A failed
update must retain or restore a usable prior executable and report failure; a
rolled-back agent may not become live against a mismatched controller.

## Acceptance builds

After building the final signed Release, build real signed variants for isolated
upgrade, downgrade, and same-version/different-build tests:

```powershell
./scripts/Build-UpdateFixtures.ps1
```

These variants use the same production source and publisher. They do not add an
updater bypass. They are test artifacts, excluded from the distribution and Git.
Application tests run through the Hyper-V broker; build and pure unit-test tools
may run on the development host. See `ACCEPTANCE_MATRIX.md` for the required proof
and `VALIDATION.md` for the scope actually established by completed runs.
