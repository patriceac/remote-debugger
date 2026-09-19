# Passphrase setup and remote migration

Quit any running Remote Debugger instance, including its tray instance, then
launch `RemoteDebugger.exe --security` on the controlling PC. Choose a strong,
unique passphrase (several random words, at least 16 characters), confirm it,
and click **Create protected setup**. The Windows account remembers access.
The passphrase is not hardcoded, sent to the relay, or saved by the app.

The relay administrator first deploys the current relay and prepares a fresh
`PROTECTED_ACCESS_KEY`. `scripts/Prepare-RelaySecurity.ps1` performs that step
with an authenticated Wrangler installation and saves the same credential with
DPAPI on the controlling PC. It retains an existing preparation on retry. Keep
the old `ACCESS_KEY` until the intended computers have migrated. Preparation
does not give an old installer access to the new credential or directory.

## Existing computers

Select **Update existing computers**. Receiving PCs must be online with support
enabled and must have completed their initial administrator setup. No one
enters a passphrase on those PCs.

The controller authenticates with the old credentials, uses the existing signed
binary updater, then stages a second relay connection with fresh relay and
pairing credentials. It pins the receiving PC's existing certificate, proves
the new pairing secret, and saves recovery authorization locally before
committing. The receiver saves the new settings under DPAPI, invalidates old
sessions, and closes the legacy route. The temporary migration session is then
released, leaving support enabled for a fresh connection.

Only the staged connection may commit the switch. It cannot issue normal
support operations before commit. Failed staging leaves the old settings in
place; an uncertain commit can be retried through the saved protected route.
Temporary staging expires after ten minutes. The receiver accepts only a fresh
profile on its existing relay; it cannot be reverted to legacy access through
this operation.

Progress is saved in `security-migration.dpapi`. Offline or failed computers
remain pending. Discovery only lists online support sessions, so an empty list
does not prove that all intended PCs have migrated. Verify your device inventory
before retiring legacy access. New session routing IDs can produce another row;
matching authenticated certificate identities are reconciled after migration.

For a targeted remote rollout from the signed Release:

```powershell
RemoteDebugger.exe cli security-status
RemoteDebugger.exe cli security-migrate --host RD-0123-4567-89AB-CDEF
```

Omit `--host` to migrate all currently discovered/pending computers. CLI output
contains names and states only. `--data-root` selects a separate Windows-protected
configuration. No passphrase or key is passed in command arguments.

After every intended PC is protected, the relay administrator removes the
legacy `ACCESS_KEY` secret. `PROTECTED_ACCESS_KEY` remains active. Old installers
then fail relay authentication entirely. Before that retirement, each migrated
PC is already isolated from legacy discovery, channel access and pairing;
unmigrated PCs still have the previous exposure. Losing the old access before
an offline PC is migrated requires local recovery on that PC.

## New installations

Creating the setup writes `%LOCALAPPDATA%\RemoteDebugger\RemoteDebugger-Protected.rdrelay`.
Build the private installer with `scripts/Build-Installer.ps1 -Sign`. The builder
rejects plaintext profiles. An alternate encrypted file can be supplied using
`-InternetProfilePath`.

The installer stages only ciphertext. On first normal launch, the Security
window asks for the passphrase once for that Windows account. Successful unlock
saves connection credentials with DPAPI CurrentUser; ordinary connections and
reinstalling the same profile require no further passphrase. An incorrect
passphrase leaves existing access unchanged. Portable users can stage the same
file with `cli internet-import --file PATH` and unlock it in the GUI.

The portable envelope uses Argon2id v1.3 (64 MiB, 3 iterations, 1 lane), a fresh
16-byte salt, and AES-256-GCM with a fresh 12-byte nonce and 16-byte tag. Its
format and profile ID are authenticated. Parsing and KDF resource use are
bounded. Credentials remain random; the passphrase only protects distribution.
A copied installer permits offline password guessing, so use a strong phrase.
An already authorized or compromised Windows account can use remembered access.

Signed application updates preserve this authorization. Back up the encrypted
setup and keep its passphrase in a password manager. The app has no passphrase
recovery service. The publisher signing key stays separate from access credentials.

## Verification

Run the core/platform unit tests and relay tests. `scripts/Test-Security.ps1`
runs the signed Release GUI and CLI in the Hyper-V InternetOnly profile using a
private fixture. The migration request is restricted to the disposable guest's
observed routing ID, even when the fixture relay lists other computers. Check
the produced assertions, screenshots and broker cleanup evidence. This test
checks the security transition after binary synchronization; it does not by
itself certify every historical updater version.
