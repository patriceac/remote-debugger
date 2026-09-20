# Local MCP tools

The adapter runs on the controlling PC over stdio and invokes the signed Release
CLI's `connected` verb. It uses the existing authenticated client and verified
transfers. It adds no listener, remote component, credentials, pairing, automatic
synchronization, or administrative operation.

Requires Node 20+ to build and Remote Debugger 0.4.37+ on the controller. The normal
exact-binary requirement still applies to the receiving agent. Connect and
synchronize through the Remote Debugger application before using action tools.

## Build and configure

```powershell
./scripts/Build.ps1 -IncludeLab -Sign
./scripts/Build-Mcp.ps1
```

`artifacts/mcp` contains a portable Node runtime and the adapter. Example Codex
configuration (use absolute paths):

```toml
[mcp_servers.remote-debugger]
command = 'D:\Disk\Dev\Remote Debugger\artifacts\mcp\node.exe'
args = ['D:\Disk\Dev\Remote Debugger\artifacts\mcp\app\server.mjs']
startup_timeout_sec = 20
tool_timeout_sec = 330

[mcp_servers.remote-debugger.env]
REMOTE_DEBUGGER_EXE = 'D:\Disk\Dev\Remote Debugger\artifacts\release\RemoteDebugger.exe'
```

Use the same signed executable for the GUI and CLI. The installed
`C:\Program Files\RemoteDebugger\RemoteDebugger.exe` can also be used once updated.
`REMOTE_DEBUGGER_CONNECTION` optionally fixes a local DPAPI-protected profile;
otherwise the adapter uses the GUI's current profile. Restart the MCP server in
Codex settings after configuration changes. See the
[official MCP configuration reference](https://developers.openai.com/codex/mcp/).

## Workflow

Call `remote_status` first and pass its opaque `targetId` to subsequent tools.
This binds the certificate/credential and agent process/session start. The CLI
loads the profile once, rejects another computer before contacting it, and
rejects a changed session before dispatch. Status also reports binary mismatches;
other operations fail until the binaries match, without attempting an update.

| Tool | Purpose |
| --- | --- |
| `remote_status` | Current computer, session and binary match state |
| `remote_system` | CPU, RAM, uptime and storage |
| `remote_processes` | Processes and sampled resource use |
| `remote_process_info` | Actual executable path, hash and version for a PID |
| `remote_file_info` | Remote size, hash and version |
| `remote_screenshot` | Fresh JPEG image content and capture geometry |
| `remote_run` | Named executable and argument array |
| `remote_upload` | Resumable upload with SHA-256 verification |
| `remote_download` | Verified download before replacing the local destination |

Results include `id`, `targetId`, `machine`, `ok`, `rpcOk`, `exitCode`, `data`,
`error`, and `message` where available. A nonzero or missing command exit code is
an MCP error even when the RPC succeeded. Screenshot bytes appear as image content,
with metadata in the structured result.

`timeoutSeconds` is an overall 1–300 second budget including session checks.
MCP cancellation closes the CLI input pipe; the CLI cancels work and attempts a
bounded cancellation RPC for a running command. A transport failure may leave an
uncertain outcome. Inspect state, keep the returned ID, and supply the same
`requestId` for an uncertain command retry in the same session. The existing
agent replay cache prevents repeated execution; the adapter never automatically
retries commands. Transfers use existing hash-bound resume state.

Read-only annotations help clients present permissions but do not enforce the
operator's intent. Explicitly naming PowerShell or cmd still launches a shell;
commands must stay within the authorized support task. Maintenance, desktop
input and session-ending operations remain in the existing CLI workflow.

## Guarded CLI and tests

`cli connected --request FILE` accepts the usual UTF-8 JSON request envelope,
plus `targetId` for every operation except `status`. Use `--request -` for stdin.
The MCP adapter adds `--cancel-on-stdin-close`: it sends one JSON line, holds stdin
open during execution, and closes it to cancel. Supported operation names match
the table, with `command` for `remote_run` and `localPath`/`remotePath` for transfers.

The Node unit tests use the SDK's in-memory transport and fake CLI responses.
`ConnectedCliTests` test guards, arguments, cancellation and success rules without
launching an application. `./scripts/Test-Mcp.ps1` uses the Hyper-V SYSTEM broker
in a disconnected VM: real stdio handshake, no-session behavior, all nine tools
against a signed loopback agent, verified transfers and same-ID command replay.
