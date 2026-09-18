---
name: remote
description: "Use Remote Debugger's authenticated CLI to support or diagnose the authorized Windows PC already connected to the current remote session."
---

# Remote

Use this skill for an existing Remote Debugger support session: inspecting, diagnosing, transferring a file to, or operating the connected Windows PC whose owner has authorized the work. The connection may be direct or relay-backed. This is not a session-setup, relay-configuration, general remote-shell, or unattended-administration workflow.

## Use the connected machine

- Apply the request only to the Windows PC already connected to the current Remote Debugger session. If no machine is connected, report that and stop; do not discover, establish, or select a session.
- Use the signed Release `RemoteDebugger.exe` on the controlling PC. Do not use a development build for an actual support session.
- Use the CLI's current authenticated connection. Do not discover, establish, synchronize, or switch sessions from this skill.

## Diagnose before changing

Start with `status` to verify the connected machine, then use `system`, `processes`, and, when visual evidence helps, a fresh `cli screenshot`. For an application, use `process.info` to record its actual path, hash, and version before acting. If `status` cannot confirm the connected machine, stop instead of choosing or connecting to another target.

Send structured operations through `cli call --request <file>` (or `--request -` for UTF-8 JSON on standard input). Give consequential requests a UUID `id` and retain the returned JSON. A remote action is successful only when both `ok` is true and any returned `data.exitCode` is zero.

## Make scoped changes safely

- Keep actions within the user's stated support goal. `command` launches a named executable with explicit arguments; it does not imply shell access. Use a bounded operation timeout.
- Upload only the requested artifact to a versioned workspace path. Confirm the returned SHA-256 or `file.info` before launching it. Use `cli download` for logs or results; it verifies SHA-256 before replacing the local destination.
- Prefer `ui.inspect` before `ui.click`, and use a fresh screenshot/returned geometry before pointer input. Secure desktops, Ctrl+Alt+Delete, clipboard, and audio are outside Remote Debugger control.
- Use `debug.attach` for bounded native event evidence and `debug.dump` for a minidump. Symbol/source-level debugging requires an explicitly installed external debugger.
- `maintenance.session` can run administrator-level work only when `maintenance.status` reports it available and the receiving owner has enabled **Admin maintenance**. Require explicit user authorization. If it is disabled or unavailable, report that and stop; do not run `platform-provision`, re-enable maintenance, or use elevation merely to avoid a failed normal operation.

## Retry and finish deliberately

For a dropped connection or timeout, do not repeat a consequential operation with a new UUID or establish a replacement session. If the same authenticated session reconnects, inspect the state and retry the same request ID while the same agent process remains alive. Otherwise report that no machine is connected and stop. Preserve remote errors and messages for the operator.

Leave the session available when further support is expected. Send `session.disconnect` only when the user asks to disconnect the current session; send `session.end` only when the user asks to end assistance, because it revokes access and stops maintenance. Disconnection or termination must leave the Remote Debugger application running and available for a new connection; there is no automatic-exit countdown.
