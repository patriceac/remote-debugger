# Internet support

Internet mode connects both PCs outward over HTTPS/WebSockets (port 443). No VPN, router port forwarding, or incoming Windows firewall rule is required for the relay. LAN discovery remains available separately.

## Set up your devices

1. Run your personal `RemoteDebugger-<version>-Private-Setup.exe` on each PC. It embeds the `.rdrelay` settings and imports them automatically for the current Windows user, including during silent installation. No separate setup file or import step is needed.
2. Open the app and click **Enable support** on the assisted PC. Approve Windows administrator setup on first use. Opening the app alone does not grant access. The client then appears by computer name on your other PCs.
3. On the controlling PC, choose **Take control**, select the computer and click **Connect**. No support ID, IP address, authorization code, or Internet setup screen is required.

The installer contains a relay credential and a separate private authentication secret shared by your 3–5 devices. It stores both with Windows DPAPI protection and deletes its temporary plaintext profile after import. The authentication secret is never uploaded to Cloudflare and is not the publisher's signing private key. A PC with this private installer can connect while the receiving user has enabled support. Keep private installers and `.rdrelay` files out of public releases, repositories and logs.

Use the private installer for the normal setup and for relay configuration updates. The developer CLI can still import a `.rdrelay` profile for a portable build; this is not part of the app's setup flow.

Routing IDs remain internal and change between support sessions. **End support** immediately revokes access; the computer disappears on a subsequent discovery refresh. After an abrupt disconnection, presence can remain stale for up to a minute before the next refresh. A fresh launch waits for **Enable support** again. Transient relay failures reconnect with backoff while the existing support-session grace period applies. Private mode uses outbound internet connections and keeps local listeners on loopback.

`--loopback-only` also disables internet registration. Importing a setup file does not enable Windows administrator maintenance; the existing separate Windows provisioning rules still apply.

## CLI

```powershell
RemoteDebugger.exe cli internet-import --file RemoteDebugger-Internet.rdrelay
RemoteDebugger.exe cli discover
RemoteDebugger.exe cli pair --host RD-0123-4567-89AB-CDEF
```

Private internet pairing reads the installer secret from protected settings, without prompting for a code. The developer CLI exposes internal routing IDs for scripting; the GUI uses computer names. LAN-only pairing still reads a code from standard input. `--data-root DIRECTORY` selects a settings directory for import, discovery and pairing. After pairing, the existing `sync`, `call`, `stream`, upload, and download commands use the protected saved connection automatically.

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

The account-connected Cloudflare plugin can upload the built `relay/dist/index.js` module with the same bindings, relay secret and SQLite migrations instead of using Wrangler OAuth. `v1` creates sessions; `v2` adds the directory. Never upload `pairingKey` to the relay or put either secret in committed configuration. `/health` reports service/protocol readiness without revealing invitations or credentials. Updating credentials requires rebuilding and running the private installer on each device.

## Security and operating limits

Cloudflare carries opaque inner TLS records. J-PAKE authenticates a session-specific secret derived from the private installer key and invitation ID, with mandatory mutual confirmation bound to the endpoint certificate and controller executable hash. There is no fallback to six-digit pairing for a private internet agent. Each registration also has an independent random owner key so another relay user cannot take over its invitation. Reconnected registrations close the previous generation's channels. Unauthenticated controllers do not receive desktop data.

The relay limits an invitation to twelve concurrent request/stream channels, binary messages to 64 KiB and each channel to 16 MiB per second. Pending channels expire. WebSocket hibernation keeps idle connections inexpensive; payloads are never written to Durable Object storage. Free-tier limits apply to the whole Cloudflare account. Requests can fail when that day's free quota is exhausted. Registered device count alone does not guarantee free operation: measure actual session duration and stream traffic.

## Validation

Run `scripts/Test-Unit.ps1` and the Worker tests before building. `scripts/Build.ps1 -IncludeLab -Sign` produces the release and acceptance Lab. `scripts/Test-Internet.ps1` requests the broker's `InternetOnly` network and tests the actual release GUI and CLI through the deployed service: discovery by name, wrong-installer rejection, code-free connection, endpoint pinning, live desktop, file integrity, and revocation. Its private profile and release executable are separate immutable read-only inputs. The automated transport fixture explicitly enables support with `--enable-support`; `scripts/Test-InternetInstaller.ps1 -Demo` exercises the normal activation button with the user handling Windows approval. Tests never fall back to host execution or another network profile.

Two product processes in one isolated guest exercise the public relay path; this does not constitute a two-site ISP or corporate-proxy compatibility test.
