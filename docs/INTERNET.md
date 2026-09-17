# Internet support

Internet mode connects both PCs outward over HTTPS/WebSockets (port 443). No VPN, router port forwarding, or incoming Windows firewall rule is required for the relay. LAN discovery remains available separately.

## Set up your devices

1. Run your personal `RemoteDebugger-<version>-Private-Setup.exe` on each PC. It embeds the `.rdrelay` settings and imports them automatically for the current Windows user, including during silent installation. No separate setup file or import step is needed.
2. Open the app and wait for **Internet ready**. The assisted PC displays a support ID such as `RD-0123-4567-89AB-CDEF`, its public IP for diagnostics, and its six-digit authorization code.
3. On the controlling PC, enter the support ID in **Take control**, enter the six-digit code, and connect.

The installer stores settings with Windows DPAPI protection and deletes its temporary plaintext profile after import. The installer itself contains the relay credential and is intended for private distribution to your 3–5 devices. It does not embed the publisher's signing private key or authorize control of a PC; the current authorization code is still required. Keep private installers and `.rdrelay` files out of public releases, repositories and logs.

For the portable executable or an installer built without bundled settings, use **Give control → Internet setup…** to import your private `.rdrelay` file once. That control also remains available for later configuration changes.

Support IDs locate a currently running invitation. New support sessions generate new IDs. Codes expire and are single-use. **End support** revokes the endpoint grant immediately. Transient relay failures reconnect with backoff while the existing support-session grace period applies. The displayed public IP is informational; it is not used to bypass routers.

`--loopback-only` also disables internet registration. Importing a setup file does not enable Windows administrator maintenance; the existing separate Windows provisioning rules still apply.

## CLI

```powershell
RemoteDebugger.exe cli internet-import --file RemoteDebugger-Internet.rdrelay
RemoteDebugger.exe cli pair --host RD-0123-4567-89AB-CDEF
```

The pairing command reads the authorization code from standard input. `--data-root DIRECTORY` selects a settings directory for import and pairing. After pairing, the existing `sync`, `call`, `stream`, upload, and download commands use the protected saved connection automatically.

## Relay deployment

The `relay/` directory contains a Cloudflare Worker and one SQLite-backed Durable Object per invitation. Deploy into a Workers Free account. It needs no KV, R2, domain purchase, or paid plan enablement.

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
  "accessKey": "YOUR_64_CHARACTER_RANDOM_HEXADECIMAL_KEY"
}
```

The account-connected Cloudflare plugin can upload the built `relay/dist/index.js` module with the same bindings, secret and `v1` SQLite migration instead of using Wrangler OAuth. Never place the secret in committed configuration. `/health` reports service/protocol readiness without revealing invitations or credentials. Rotating the relay key requires importing an updated private profile on each device.

## Security and operating limits

Cloudflare carries opaque inner TLS records; it cannot decrypt the paired endpoint stream. The existing J-PAKE exchange binds the displayed code to the endpoint certificate and controller executable hash. Each agent registration also has an independent random owner key so another relay user cannot take over its invitation. Reconnected registrations close the previous generation's channels. Unpaired controllers do not receive desktop data.

The relay limits an invitation to twelve concurrent request/stream channels, binary messages to 64 KiB and each channel to 16 MiB per second. Pending channels expire. WebSocket hibernation keeps idle connections inexpensive; payloads are never written to Durable Object storage. Free-tier limits apply to the whole Cloudflare account. Requests can fail when that day's free quota is exhausted. Registered device count alone does not guarantee free operation: measure actual session duration and stream traffic.

## Validation

Run `scripts/Test-Unit.ps1` and the Worker tests before building. `scripts/Build.ps1 -IncludeLab -Sign` produces the release and acceptance Lab. `scripts/Test-Internet.ps1` requests the broker's `InternetOnly` network and tests the actual release GUI and CLI through the deployed service: registration, wrong-code rejection, pairing, endpoint pinning, live desktop, file integrity, and revocation. Its private profile and release executable are separate immutable read-only inputs. It never falls back to host execution or another network profile.

Two product processes in one isolated guest exercise the public relay path; this does not constitute a two-site ISP or corporate-proxy compatibility test.
