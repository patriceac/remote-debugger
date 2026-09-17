# Installation, signing, and updates

## Desktop installer

`RemoteDebugger-<version>-Setup.exe` is a per-user installer. It writes beneath
`%LOCALAPPDATA%\Programs\Remote Debugger`, creates a Start menu shortcut, and
registers an uninstaller without requesting administrator privileges. Launching
the shortcut starts the application normally at medium integrity. The portable
single executable remains supported and behaves the same way.

The personal `RemoteDebugger-<version>-Private-Setup.exe` also embeds the private
internet setup file. Installation automatically imports it into the current
user's DPAPI-protected settings before the app launches, then deletes the
temporary plaintext copy. This works in interactive and silent installations.
The installer stays private because it contains the relay credential.

The desktop installer is deliberately separate from protected support setup.
Choosing **Enable on this PC** still requests one explicit Windows administrator
approval so the application can provision its Program Files copy, local broker,
and Private/LocalSubnet firewall rules. Later launches through the Start menu
automatically redirect an agent to that protected copy without another prompt.

Build the installer after producing a signed Release:

```powershell
./scripts/Build-Installer.ps1 -Sign
```

The default private build reads `dist/internet/RemoteDebugger-Internet.rdrelay`,
which is ignored by Git. Use `-InternetProfilePath PATH` for another private
profile, or `-WithoutInternetProfile` to build the distributable installer
without credentials. A missing or invalid requested profile fails the build.
The executable and publisher signing key are unchanged by profile embedding.
Run `scripts/Test-InternetInstaller.ps1` after publishing the acceptance Lab to
verify automatic configuration and first-launch relay registration in Hyper-V.

Inno Setup must be installed or its compiler path supplied with
`-CompilerPath`. Uninstalling the per-user package removes its files and Start
menu shortcut. Protected support provisioned by **Enable on this PC** is managed
separately because removing a Windows service and Program Files state requires
administrator authorization.

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

If those hashes differ, the controller transfers its signed executable, the agent
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
