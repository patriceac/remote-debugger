# Getting started

The application currently uses French interface labels. This guide keeps those labels so you can find the corresponding controls.

1. Copy `RemoteDebugger.exe` to both Windows x64 PCs.
2. On the remote PC, open **Donner le contrôle** (give control). On first use, click **Autoriser le réseau privé…** (allow the private network) and approve UAC locally. This allows connections only from the local subnet on the Private network profile. Then click **Démarrer l’agent** (start the agent). The agent uses TCP 45832 and UDP 45833. No router configuration or Internet port forwarding is required. If Windows displays a broader public/private network prompt, cancel it and use the dedicated button.
3. Click **Ouvrir l’appairage** (open pairing). An eight-digit code and the TLS certificate fingerprint appear.
4. On the controlling PC, open **Prendre le contrôle** (take control), then **Découvrir les PC** (discover PCs). Select the remote PC. If Wi-Fi blocks discovery, enter its IP address directly.
5. Compare all 64 fingerprint characters with the value shown on the remote PC, check the confirmation box, enter the code and click **Appairer** (pair). The code expires after three minutes or five incorrect attempts. Only one controller can be paired at a time.

The connection is saved for your Windows account. **Couper l’agent** (stop the agent) or closing its window ends access and cancels active operations. Applications already launched remain open. **Révoquer l’accès** (revoke access) invalidates the controller and requires pairing again. No automatic startup is installed.

## View and control the remote desktop

In **Écran distant** (remote screen), start the live stream. It targets 5 frames per second; the status bar shows the measured frame rate and bandwidth. Monitor indices start at 0; -1 shows the entire virtual desktop. Stop streaming before changing monitors.

Enable **Souris et clavier distants** (remote mouse and keyboard), then click inside the image. Pointer movement, buttons, dragging, scrolling and keys are forwarded to the remote PC. Clicking outside the image returns keyboard control to the local PC. Coordinates follow the remote desktop geometry even when the image is scaled; a resolution change invalidates old coordinates. Key combinations intercepted locally by Windows can be sent through `ui.key`. Ctrl+Alt+Delete and the UAC secure desktop are not supported.

**Actualiser l’écran** (refresh the screen) captures a new image on the remote PC. Codex can request the same fresh screenshot through the CLI independently of the live viewer.

## Deploy and repeat

In **Fichiers** (files), use a separate directory for each version, such as `deployments/MyApp/1.2.3`, then upload a file or a complete folder. The destination is beneath `%LOCALAPPDATA%\RemoteDebugger\workspace` on the remote PC. Writes are verified with SHA-256. After an interruption, repeating the upload resumes accepted chunks if the source file is unchanged.

In **Actions et diagnostics** (actions and diagnostics), `start` launches the specified relative or absolute path and returns a PID plus the binary hash and version. Use `process.info` to verify the executable associated with the running process. `stop` requests a graceful close; `mode: "force"` explicitly terminates the process tree. `restart` applies to processes launched by the current agent.

In **Processus & système** (processes and system), inspect CPU, memory and volumes. Selecting a process sets the target PID. Unavailable values are not displayed as zero. Measurements are timestamped, and CPU percentages represent a share of total logical-processor capacity.

To retrieve a log, select its path in **Fichiers**, then choose **Récupérer un fichier** (download a file). A file changed during download causes a hash verification error.

## Debugging and maintenance

`debug.attach` uses the Windows debugging API to attach, request a breakpoint, collect events and detach. `debug.dump` produces a minidump that can be downloaded. For a full interactive session, deploy an external debugger and launch it with `start`; its interface can be used through the live viewer. Remote Debugger does not include a symbol analysis engine.

`command` launches an executable with explicit arguments and returns output and an exit code, with timeout and cancellation support. To prepare a maintenance session, click **Maintenance admin (1 h)…** on the agent and approve UAC once locally with the same administrator account. A separate window shows the remaining time. The controller can then issue `maintenance.session` commands for up to one hour; `maintenance.status` reports whether authorization is active. Closing the helper, stopping the agent or revoking access cancels the session and its commands. No permanent service is installed.

`maintenance.elevated` remains a per-operation alternative: every call requires its own local UAC confirmation on the remote PC. Personal confirmations, the secure desktop and elevation under another account require local interaction. See [the validation record](VALIDATION.md) for the scope of elevation testing.

The history records operations, timestamps, success or failure and relevant binary identities. It does not retain command arguments, file contents, keystrokes, images or pairing codes. Requested output and dumps may contain sensitive information; select what you retrieve and share.
