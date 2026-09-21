# Controller CLI for Codex

Run the same Release executable on the controlling PC. It talks to the interactive agent through the same `RemoteClient` implementation as **Prendre le contrôle**. No remote action is executed by the local harness or a side channel.

## Discover and pair

```powershell
./RemoteDebugger.exe cli discover
```

The reply contains `ok` and `peers` with `name`, `host`, `port`, `fingerprint`. Discovery is an untrusted hint. Obtain the current six-digit code from **Donner le contrôle** on the receiving PC. Code-authenticated pairing binds the TLS certificate; manual fingerprint confirmation is not required.

```powershell
# Supply the code on standard input; do not embed it in a process argument or log.
$pairingCode | ./RemoteDebugger.exe cli pair --host 192.168.1.42
./RemoteDebugger.exe cli sync
```

For private internet access, unlock the personal installer's encrypted setup once in the app's Security window. Existing computers can receive credentials through remote migration. Enable support on the receiving PC, discover its internal routing address, then connect without a code:

```powershell
./RemoteDebugger.exe cli discover
./RemoteDebugger.exe cli pair --host RD-0123-4567-89AB-CDEF
./RemoteDebugger.exe cli sync
```

`--data-root DIRECTORY` selects the settings directory for `internet-import`, `discover` and `pair`; `--connection FILE` still selects the saved connection. With a private profile, discovery lists online computers and pairing authenticates using the separate installer secret. Without one, discovery and code entry retain their LAN behavior. Both internet endpoints connect outward over port 443 for relay work. Private connections prefer LAN, then the optional per-device WAN address saved in the app, then the protected relay with the pinned endpoint. The external WAN port defaults to 45832 and may be overridden; it must be forwarded to the receiving PC's TCP 45832. See [internet support](INTERNET.md).

Private discovery also returns LAN peers and their current `supportId`. Pairing to
their LAN IP derives the same private secret automatically; `--support-id RD-...`
can supply the invitation explicitly when discovery is unavailable. Saved routes
are rechecked by certificate identity. Session termination, cancellation and update
recovery calls bypass automatic synchronization so cleanup remains possible during
an interrupted update.

The pairing token is never printed. GUI and CLI share `%LOCALAPPDATA%\RemoteDebugger\controller.connection`, encrypted for the Windows user. `--connection FILE` selects a different DPAPI-protected controller profile. `--port` can be specified when pairing; the GUI uses 45832. An optional `--fingerprint SHA256` enforces a previously verified certificate pin. `sync` uses the executable actually running this CLI as the required agent binary, requiring an enrolled admin key and a strictly newer version for replacement. Normal support operations reject mismatched binaries. A normal agent restart requires fresh pairing; only a bounded planned-update grant resumes automatically. When a saved paired computer is reachable with an older or different agent binary, normal CLI operations automatically synchronize it before proceeding; the explicit `sync` verb remains available for showing the transfer result directly.

`cli platform-status` reports installed broker readiness and the provisioning receipt path. `cli platform-provision` performs the one-time administrator setup and may require local Windows consent.

`cli security-status` reports migration progress without exposing credentials.
After creating a protected setup in the GUI, `cli security-migrate` updates
reachable existing computers; `--host RD-...` restricts it to one observed route.
Both accept `--data-root`. Offline work stays pending and can be retried. See
[security setup](SECURITY_SETUP.md) before retiring the legacy relay credential.

## Structured call

The local [MCP adapter](MCP.md) uses `cli connected --request`: a guarded interface
that binds requests to the confirmed session and never automatically synchronizes
the agent. Its `remote_run` tool requests `maintenance.session` through the
provisioned administrator broker and fails without it. The ordinary `cli call`
behavior below is unchanged.

Write an ordinary UTF-8 JSON request, for example `status.json`:

```json
{
  "id": "c8b30df4-bd10-4cba-99fc-ae8114fe87be",
  "operation": "status",
  "args": {},
  "timeoutSeconds": 60
}
```

```powershell
./RemoteDebugger.exe cli call --request status.json
# --request - reads JSON from standard input.
```

`id` is optional; when supplied it must be a UUID. `timeoutSeconds` is clamped to 1–300. The response is one JSON object:

```json
{"id":"c8b30df4-bd10-4cba-99fc-ae8114fe87be","ok":true,"data":{"version":"0.2.0"},"error":null,"message":null}
```

Exit codes: **0** success, **1** remote operation failure, **2** transport/input/cancellation failure. Error codes include `access_denied`, `pairing_denied`, `permission_denied`, `operation_failed`, `cancelled_or_timeout`, `id_conflict`, and `session_limit`. Preserve the JSON message for the operator. Do not report command success without checking both RPC `ok` and `data.exitCode`.

Ctrl+C attempts cancellation of an in-flight `call`. Another controller process can send `cancel` with the original request UUID. A dropped connection can leave an operation in progress; reconnect and inspect state, or retry the same UUID while the same agent process is still running. Do not blindly retry consequential operations with a new UUID. The agent retains up to 2,048 mutation replies per process lifetime. After restart this cache is empty.

## Operation arguments

| Operation | `args` | Result / effect |
|---|---|---|
| `status` | `{}` | Machine/user, tool version, agent PID, workspace, elevation status |
| `system` | `{}` | Timestamped CPU sample, available/total RAM, volumes/free space, uptime |
| `processes` | `{}` | Timestamped sampled process CPU/memory, window title, response state |
| `process.info` | `{"pid":1234}` | Process start time, actual image path/SHA-256/file version, thread count |
| `files` | `{"path":"deployments"}` | Up to 1,000 immediate children; blank path means workspace |
| `file.info` | `{"path":"deployments/v1/App.exe"}` | Size, SHA-256, file version |
| `start` | `{"path":"deployments/v1/App.exe","arguments":[]}` | PID and launched binary identity |
| `stop` | `{"pid":1234,"mode":"graceful"}` | Close main window and wait; `force` explicitly kills the process tree |
| `restart` | `{"pid":1234,"mode":"graceful","arguments":[]}` | Stop/start an application previously launched by this agent process |
| `windows` | `{"pid":1234}` or `{}` | Window handles, owning PIDs and names |
| `ui.inspect` | `{"pid":1234}` | Up to 250 UI Automation controls, IDs/names/types; no password values |
| `ui.click` | `{"pid":1234,"automationId":"saveButton"}` | Invoke a unique control; `name` is an alternative selector |
| `ui.text` | `{"pid":1234,"text":"test"}` | Focus target and type Unicode text; PID 0 uses current foreground |
| `ui.key` | `{"pid":1234,"key":"CTRL+A"}` | Named key/chord, such as ENTER, TAB, ESCAPE, F1–F12, letters |
| `ui.mouse` | `{"pid":1234,"x":500,"y":300}` | Focus target and left-click absolute desktop pixels |
| `monitors` | `{}` | Monitor indices, bounds, device and primary flag |
| `screenshot` | `{"monitor":0,"quality":85}` | Fresh JPEG in base64, capture time, geometry and capture/encode duration |
| `ui.input` | See below | Explicit pointer/key transitions without changing foreground |
| `network` | `{}` | Network IP configuration and adapter counters as JSON in `stdout` |
| `services` | `{}` | Service names, states and start types as JSON in `stdout` |
| `events` | `{"log":"Application","count":30}` | Application/System events, 1–100, JSON in `stdout` |
| `command` | `{"file":"whoami.exe","arguments":[]}` | Exit code and bounded stdout/stderr; no implicit shell |
| `maintenance.status` | `{}` | Actual administrator maintenance availability for this support session |
| `maintenance.session` | Same as command | Administrator command through the provisioned local broker |
| `maintenance.elevated` | Same as command | Compatibility operation using the provisioned broker |
| `debug.attach` | `{"pid":1234,"seconds":3}` | Real native attach, breakpoint, events, detach; 1–30 seconds |
| `debug.dump` | `{"pid":1234}` | MiniDumpWriteDump output under workspace/diagnostics |
| `history` | `{}` | Last 200 intervention metadata records with relevant version evidence |
| `cancel` | `{"id":"ORIGINAL-UUID"}` | Request cancellation of a running operation |
| `session.heartbeat` | `{}` | Current session, actual binary hashes, maintenance and agent PID |
| `session.disconnect` | `{}` | Release input and begin the ten-minute reconnect deadline |
| `session.end` | `{}` | Cancel update replacement, end maintenance and access, then leave the agent open and idle |

CPU values are percentages of total logical-processor capacity, not a single core. `null` means unavailable/newly created/inaccessible, never zero. Check `sampleStartUtc`, `sampleEndUtc` and `intervalMs`. A process without a main window has no GUI response state (`responding: null`). System and process requests are independent samples.

File reads and uploads accept full paths accessible to the agent user. Relative upload paths stay beneath the agent workspace; `..` cannot escape it. Namespaces and drive letters belong to the remote PC.

## Wake-on-LAN

For Wake-on-LAN, use `cli wake --mac 00:11:22:33:44:55`, optionally with
`--address 192.168.1.255 --port 9`. This sends from the local PC without a saved
connection. With no address it broadcasts on active IPv4 Ethernet/Wi-Fi networks.
An authenticated support session also accepts `wake.info` and `wake` operations;
`wake` takes `macAddress`, optional `destination`, and optional `port` arguments.
Success confirms packet transmission, not startup. See [Wake-on-LAN](WAKE_ON_LAN.md).

## Files

```powershell
./RemoteDebugger.exe cli upload --file ./App.exe --path deployments/App/1.2.3/App.exe
./RemoteDebugger.exe cli download --path 'deployments/App/logs/app.log' --file ./app.log
```

Repeat the same upload after transport interruption to resume the accepted offset when the source hash is unchanged. The controller's resume metadata is DPAPI protected. Uploads use 256 KiB chunks, at most 16 GiB per file, and verify the complete SHA-256 before promotion. Downloads use a temporary local file and verify SHA-256 before replacing the destination; retry if the source changed.

Low-level resumable upload operations are also available for automation:

| Operation | Arguments |
|---|---|
| `upload.begin` | `transfer` (32 hex UUID), `path` (workspace-relative or full remote path), `size`, `sha256` |
| `upload.status` | `transfer`; returns accepted `offset` and `size` |
| `upload.chunk` | `transfer`, `offset`, `data` (base64, max 256 KiB decoded) |
| `upload.commit` | `transfer`; requires full size/hash, then promotes file |
| `upload.abort` | `transfer`; deletes this transfer's uncommitted files |

Identical repeated chunks are accepted; conflicting/overlapping data is rejected. There is no implicit archive extraction or remote execution during upload.

## Fresh screenshots and continuous stream

```powershell
./RemoteDebugger.exe cli screenshot --file ./remote-now.jpg --monitor 0
./RemoteDebugger.exe cli stream --seconds 10 --fps 5 --monitor 0 --report ./stream.json --last-frame ./last.jpg
```

`screenshot` always asks the remote machine for a fresh frame, writes it locally, and returns capture metadata plus measured request round-trip time. It does not reuse the human viewer's image.

`stream` reports consumed FPS, distinct frame count, consumed payload bitrate estimate, remote capture/encode time, inter-frame p95 and controller CPU. CLI measurements exclude GUI decode/presentation; the GUI measures its own presented cadence. The interactive controller's live viewer negotiates H.264 over the encrypted stream first, with libx264 configured for low latency and no B-frames; if the encoder is unavailable or remains slower than its frame budget, the agent switches the same session to binary JPEG and the controller shows the active mode. The CLI `stream` command remains the legacy JPEG measurement path. There is one receipt-acknowledged frame in flight and only the newest decoded frame retained for presentation, target up to 5 FPS. Slow consumers skip decoded frames instead of replaying an encoded H.264 dependency chain. Monitor -1 is the full virtual desktop. No audio, clipboard sync, hardware codec or secure desktop control is included.

For a bounded slow-consumer diagnostic, add `--present-delay-ms 1200` (0–5000 ms; zero by default). The report includes `presentedSequences` and `framesSkipped`; its legacy `receivedFps` and payload-rate fields measure frames delivered to the consumer, excluding skipped frames. This diagnostic uses the same receiver and latest-frame delivery path as the GUI; it does not measure GUI rendering.

Pointer events bind to the `geometry.layoutId` returned by a fresh frame:

```json
{"operation":"ui.input","args":{"kind":"down","button":"left","x":600,"y":400,"layoutId":"VALUE_FROM_FRAME"}}
```

`kind`: `move`, `down`, `up`, `wheel` (with signed `delta`), `keyDown`/`keyUp` (with Windows `virtualKey` integer), or `release`. Send releases; the agent also has a three-second stuck-input watchdog. Coordinates are source desktop pixels including negative monitor origins. The GUI handles image scaling and letterboxing automatically. Stale geometry is rejected before pointer input.

## Practical debugging loop

Deploy to a unique version directory, call `start`, then `process.info` to confirm actual image/hash/version. Inspect controls or request a fresh screenshot. Send input, retrieve result/log files, record the result alongside the returned identity, then stop and deploy the next version. Use `debug.attach` for native event evidence or `debug.dump` to collect a dump; use an explicitly installed external debugger for symbol/source-level stepping. Authentication that requires personal approval stays interactive on the remote PC.
