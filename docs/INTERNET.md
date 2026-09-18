# Internet support

Internet mode uses HTTPS/WebSockets (port 443) for discovery, pairing and relay fallback. After the authenticated pairing handshake, the controller asks the agent for direct LAN and public-IP candidates and probes them over the endpoint's TLS port. A successful probe is saved and all later RPC, screen, input and transfer traffic uses the direct TCP path; the relay remains an automatic fallback if that path becomes unreachable. No VPN, router port forwarding, or incoming Windows firewall rule is required for the relay. When the relay is unavailable, the app automatically falls back to the local UDP/TCP path if local support has been provisioned.

## Set up your devices

1. Run your personal `RemoteDebugger-<version>-Private-Setup.exe` on each new PC. It installs the single Program Files application, removes any legacy per-user copy, and embeds an encrypted `.rdrelay` setup for the current Windows user, including during silent installation. Enter your setup passphrase once in the Security window at first normal launch. Existing PCs can instead receive new credentials through [remote security migration](SECURITY_SETUP.md).
2. Open the app and click **Enable support** on the assisted PC. Approve Windows administrator setup on first use. Opening the app alone does not grant access. The client then appears by computer name on your other PCs.
3. On the controlling PC, choose **Take control**, select the computer and click **Connect**. No support ID, IP address, authorization code, or Internet setup screen is required.

The installer also starts the app in the system tray when you sign in to Windows. Open it from the tray to enable support or take control. A normal Start menu launch opens the window immediately.

The installer contains passphrase-encrypted relay and pairing credentials. After successful unlock, the app remembers credentials with Windows DPAPI protection; it does not save the passphrase. The pairing secret is never uploaded to Cloudflare and is separate from the publisher's signing key. Possessing the installer alone no longer supplies usable connection credentials. Keep private installers and `.rdrelay` files out of public releases, repositories and logs, and use a strong passphrase against offline guessing.

Use the private installer for the normal setup and for relay configuration updates. The developer CLI can still import a `.rdrelay` profile for a portable build; this is not part of the app's setup flow.

Routing IDs remain internal and change between support sessions. **End support** immediately revokes access; the computer disappears on a subsequent discovery refresh. After an abrupt disconnection, presence can remain stale for up to a minute before the next refresh. A fresh launch waits for **Enable support** again. Transient relay failures reconnect with backoff while the existing support-session grace period applies. If local support is provisioned, the agent also prepares its LAN listener and firewall rule; the controller then discovers it locally and authenticates with the same private session identity. Direct WAN use additionally requires the advertised public IP and TCP 45832 to be reachable through the router and any upstream firewall; otherwise the saved relay route is used.

`--loopback-only` also disables internet registration. Importing a setup file does not enable Windows administrator maintenance; the existing separate Windows provisioning rules still apply.

## CLI

```powershell
RemoteDebugger.exe cli internet-import --file RemoteDebugger-Internet.rdrelay
RemoteDebugger.exe cli discover
RemoteDebugger.exe cli pair --host RD-0123-4567-89AB-CDEF
```

Private internet pairing reads the installer secret from protected settings, without prompting for a code. The developer CLI exposes internal routing IDs for scripting; the GUI uses computer names. LAN-only pairing still reads a code from standard input. `--data-root DIRECTORY` selects a settings directory for import, discovery and pairing. After pairing, the controller probes the authenticated agent's direct candidates and stores the selected endpoint alongside the relay fallback. The existing `sync`, `call`, `stream`, upload, and download commands use the protected saved connection automatically.

## Relay deployment

The `relay/` directory contains a Cloudflare Worker, one SQLite-backed Durable Object per invitation, and a small private directory. Only authenticated requests can list computers; the directory checks live agent connections and stores at most 32 recent routing IDs. Deploy into a Workers Free account. It needs no KV, R2, domain purchase, or paid plan enablement.

```powershell
cd relay
npm ci
npm test
npm run check
npm run build
npx wrangler login
npx wrangler deploy
npx wrangler secret put ACCESS_KEY
```

Use a cryptographically random 32-byte key encoded as 64 hexadecimal characters. Store the same key in your private profile, alongside the deployed HTTPS origin:

```json
{
  "relayUrl": "https://YOUR-WORKER.YOUR-SUBDOMAIN.workers.dev",
  "accessKey": "YOUR_64_CHARACTER_RANDOM_HEXADECIMAL_RELAY_KEY",
  "pairingKey": "A_DIFFERENT_64_CHARACTER_RANDOM_HEXADECIMAL_KEY"
}
```

The account-connected Cloudflare plugin can upload the built `relay/dist/index.js` module with the same bindings, relay secrets and SQLite migrations instead of using Wrangler OAuth. `v1` creates sessions; `v2` adds the directory. Never upload `pairingKey` to the relay or put either secret in committed configuration. `/health` reports service/protocol readiness without revealing invitations or credentials. `ACCESS_KEY` is the legacy credential; `PROTECTED_ACCESS_KEY` serves the protected directory. During migration, both work in separate scopes. Remove `ACCESS_KEY` after every intended PC is migrated. See [the security migration procedure](SECURITY_SETUP.md).

## Security and operating limits

Cloudflare carries opaque inner TLS records. J-PAKE authenticates a session-specific secret derived from the private installer key and invitation ID, with mandatory mutual confirmation bound to the endpoint certificate and controller executable hash. There is no fallback to six-digit pairing for a private internet agent. Each registration also has an independent random owner key so another relay user cannot take over its invitation. Reconnected registrations close the previous generation's channels. Unauthenticated controllers do not receive desktop data.

The relay limits an invitation to twelve concurrent request/stream channels, binary messages to 64 KiB and each channel to 16 MiB per second. Pending channels expire. WebSocket hibernation keeps idle connections inexpensive; payloads are never written to Durable Object storage. Free-tier limits apply to the whole Cloudflare account. Requests can fail when that day's free quota is exhausted. Registered device count alone does not guarantee free operation: measure actual session duration and stream traffic.

## Validation

Run `scripts/Test-Unit.ps1` and the Worker tests before building. `scripts/Build.ps1 -IncludeLab -Sign` produces the release and acceptance Lab. `scripts/Test-Internet.ps1` requests the broker's `InternetOnly` network and tests the actual release GUI and CLI through the deployed service: discovery by name, wrong-installer rejection, code-free connection, endpoint pinning, live desktop, file integrity, and revocation. Its private profile and release executable are separate immutable read-only inputs. The automated transport fixture explicitly enables support with `--enable-support`; `scripts/Test-InternetInstaller.ps1 -Demo` exercises the normal activation button with the user handling Windows approval. Tests never fall back to host execution or another network profile.

Two product processes in one isolated guest exercise the public relay path; this does not constitute a two-site ISP or corporate-proxy compatibility test.

For a user-operated host-to-VM demo, `scripts/Test-InternetInstaller.ps1 -Demo -LeaveRunning` validates tray startup and records the live connection, then leaves it available until the client exits or the broker's two-hour execution deadline. The running demo is live application evidence, not a completed cleanup qualification.
