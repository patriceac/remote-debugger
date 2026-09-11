# Architecture and trust model

`RemoteDebugger.Core` contains bounded JSON framing, path confinement, constant-time checks, pairing expiry and coordinate mapping. `RemoteDebugger` contains the Windows GUI, controller CLI, TLS transport, interactive agent, native input/debugging, resource sampling and explicit UAC helper. `RemoteDebugger.Lab` is a separate integration test executable; it is never included in the distributable Release.

## Identity and access

The visible agent listens on IPv4 TCP 45832 and answers small UDP discovery requests on 45833. Discovery is an untrusted hint. The operator must compare the SHA-256 certificate fingerprint on the agent with the controller's value before pairing. The controller pins that exact certificate for every RPC and stream. TLS 1.2/1.3 protects transport. Windows Schannel uses a user key container, with the durable PFX protected by DPAPI CurrentUser.

A locally opened, one-use eight-digit code lasts three minutes and locks after five failures. Pairing replaces the previous controller grant. The resulting 256-bit bearer token is protected by DPAPI on the controller; only its SHA-256 hash is retained by the agent. Revocation clears that grant and cancels active operations. Possession of the paired Windows account grants full interactive-user access; this is not a sandbox against the paired operator. Never expose the agent ports on the public Internet.

The agent window is mandatory. There is no hidden unattended startup, service, cloud account, telemetry, or credential extraction. The optional one-hour administrator session is described below. The alternative per-command `maintenance.elevated` helper is a separate short-lived copy invoked with Windows `runas`; it consumes a DPAPI-protected expiring job and produces a protected result. That per-command helper has its own four-minute deadline and cancellation marker. It assumes the same Windows administrator account; elevation using a different account cannot decrypt its job.

## Commands and reconnects

Each RPC has a UUID and a 1–300 second execution budget. Commands use an executable plus argument array without implicit shell interpolation. Shell use must be explicit, for example `cmd.exe` with `/c`. Command output is bounded at 256 Ki characters per stream; extra data is drained and a truncation marker is returned. Cancellation kills the launched command process tree. Interactive applications launched with `start` deliberately outlive a disconnected controller.

The agent caches up to 2,048 consequential mutation replies during its lifetime. Reusing a UUID and identical operation/arguments returns the original result; conflicting reuse is rejected. Read-only calls, naturally idempotent upload chunks and live input are not retained in that cache. At the limit, new cached mutations fail explicitly until the agent restarts. Do not replay uncertain mutations after an agent restart: inspect the actual target state first. Connection loss does not imply a command did not execute.

Uploads use 256 KiB chunks, explicit offsets, resumable local transfer state and a final SHA-256 check before replacement. Writes are confined beneath the agent workspace; path traversal, alternate data streams and reparse-point traversal are rejected. Uploads support up to 16 GiB each. Successful downloads are only promoted after hash verification. Abandoned partial uploads can be removed with `upload.abort`.

## Desktop stream

The human controller consumes a continuous JPEG stream over pinned TLS with a 5 fps target, quality 65 and maximum encoded width 1920. A presentation acknowledgement keeps at most one frame in flight. Backpressure lowers measured cadence rather than building a delayed queue. The UI displays actual presentation cadence and estimated application payload bandwidth; TLS/TCP overhead is not included. The stream reconnects at its five-minute session boundary. Other connection errors are visible and can be retried.

Fresh screenshots are separate RPCs and are captured after their request arrives. Each frame contains capture timestamp, source desktop rectangle, encoded dimensions and a layout identifier. Letterbox coordinates map back to source pixels; stale layout input is rejected. Per-monitor DPI awareness prevents controller zoom from changing source coordinates. Held input is released on stream end/revocation/focus loss, with a three-second watchdog for lost release events. Secure desktop/locked session input is refused. The product sends neither secure-attention sequences nor credentials automatically.

## Debugging and evidence

`debug.attach` uses `DebugActiveProcess`, `DebugBreakProcess`, `WaitForDebugEvent`, `ContinueDebugEvent` and `DebugActiveProcessStop`. It returns observed events and an explicit detach outcome. A successful log read is never treated as debugger proof. `debug.dump` uses `MiniDumpWriteDump`. Dumps can contain memory-derived sensitive information and are only created on explicit requests.

Implementation references: [Microsoft SslStream troubleshooting](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting), [DEBUG_EVENT](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-debug_event), [Windows high-DPI reference](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-reference).

## Administrator session

The agent's explicit local maintenance button launches a separate visible helper through UAC, for at most one hour. The helper accepts commands only over a random local named pipe with an explicit current-user ACL and a kernel-verified client PID equal to its parent agent. The agent verifies the helper server PID. The helper checks the parent start time and executable path before serving, exits when its parent exits, and cancels work on expiry, window closure or the local stop marker. The interactive agent remains unelevated and is the only network listener. Pairing revocation and agent shutdown close this permission window. Each command retains its own deadline; disconnecting its per-command pipe cancels its process tree. An inactive session rejects remote admin requests without opening UAC.

This avoids repeated local UAC prompts during one deliberately prepared maintenance session. It does not claim unattended permanent administration or secure-desktop input. A different Windows account used for elevation cannot use the same-user IPC ACL. The older `maintenance.elevated` operation remains an explicitly per-operation alternative. See the validation record for which elevated paths were actually exercised.
