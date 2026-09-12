---
name: remote
description: "Use Remote Debugger's authenticated CLI to support or diagnose an authorized Windows PC on a trusted local network."
---

# Remote

Use this skill for a Remote Debugger support session: inspecting, diagnosing, transferring a file to, or operating a Windows PC whose owner has authorized the work. It is not a general remote-shell, Internet relay, or unattended-administration workflow.

## Establish the session

- Use the signed Release `RemoteDebugger.exe` on the controlling PC. Do not use a development build for an actual support session.
- Discover peers with `RemoteDebugger.exe cli discover`, then pair only to the user-selected host. Obtain the six-digit code from the receiving PC and pipe it on standard input, for example: `Read-Host | & $exe cli pair --host 192.168.1.42`. Never put a pairing code, token, capture, or diagnostic containing personal data in a command argument, transcript, or chat response.
- Run `cli sync` after pairing and before normal operations. A mismatched agent binary must be synchronized rather than worked around.
- The connection profile is DPAPI-protected for the current Windows user. Use `--connection` only when the user has identified a separate intended profile.

## Diagnose before changing

Start with `status`, `system`, `processes`, and, when visual evidence helps, a fresh `cli screenshot`. For an application, use `process.info` to record its actual path, hash, and version before acting. Treat discovery as an untrusted hint; code-authenticated pairing is the identity check.

Send structured operations through `cli call --request <file>` (or `--request -` for UTF-8 JSON on standard input). Give consequential requests a UUID `id` and retain the returned JSON. A remote action is successful only when both `ok` is true and any returned `data.exitCode` is zero.

## Make scoped changes safely

- Keep actions within the user's stated support goal. `command` launches a named executable with explicit arguments; it does not imply shell access. Use a bounded operation timeout.
- Upload only the requested artifact to a versioned workspace path. Confirm the returned SHA-256 or `file.info` before launching it. Download logs/results to a local destination and verify the reported hash.
- Prefer `ui.inspect` before `ui.click`, and use a fresh screenshot/returned geometry before pointer input. Secure desktops, Ctrl+Alt+Delete, clipboard, and audio are outside Remote Debugger control.
- Use `debug.attach` for bounded native event evidence and `debug.dump` for a minidump. Symbol/source-level debugging requires an explicitly installed external debugger.
- `maintenance.session` and `platform-provision` can run administrator-level work. Require explicit user authorization and check `maintenance.status` or `platform-status` first; never use them merely to avoid a failed normal operation.

## Retry and finish deliberately

For a dropped connection or timeout, do not repeat a consequential operation with a new UUID. Reconnect, inspect the state, then retry the same request ID while the same agent process remains alive. Preserve remote errors and messages for the operator.

Leave the session available when further support is expected. Send `session.disconnect` only when a temporary reconnect grace period is wanted; send `session.end` only when the user asks to end assistance, because it revokes access and stops maintenance.
