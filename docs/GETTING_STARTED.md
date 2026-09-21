# Getting started

For internet access, use the personal **Private-Setup** installer on each PC. It configures the relay automatically; follow [internet support](INTERNET.md) to connect. If the relay is unavailable, the private setup can fall back to LAN discovery after local support/firewall provisioning.

The app automatically uses the Windows display language: French, English or Spanish, with English for other languages. Use **Language** at the bottom of the sidebar to select **System default**, **English**, **Français** or **Español**. The choice applies immediately, keeps the current session and is remembered next time. Each PC selects its own language. The instructions below use the English labels. Use the same signed Release on both Windows x64 PCs.

1. On each PC, run the installer for a machine-wide Program Files installation, or copy and launch the portable `RemoteDebugger.exe`. The installer removes any legacy per-user installation and needs one administrator approval. Launch Remote Debugger on the receiving PC. **Give control** is the default: the agent starts automatically and displays a six-digit code and its remaining validity. Leading zeroes are significant. The code changes every five minutes until paired.
2. On first use, **Enable support** performs the one-time Windows administrator setup. It installs the local broker, enrolls the publisher, and configures the Program Files installation for protected maintenance. Thereafter Private-network authorization, LAN fallback and administrator maintenance are automatic. Uncheck **Enabled** beside **Admin maintenance** on the receiving PC to close and block administrator maintenance; the choice is saved until you turn it back on. The application does not change a Public network to Private.
3. On the controlling PC choose **Take control**. Nearby PCs appear automatically. Select the receiving PC, or enter its IP address if discovery is unavailable.
4. Enter the receiving PC's code and press Enter or **Connect**. Pairing authenticates the encrypted connection using the code, and subsequent connections pin that identity.
5. Wait for binary synchronization. If necessary, the agent silently receives and restarts into the exact signed executable running on the controller. Both PCs show actual transfer bytes and percentage; verification and restart are separate stages after the transfer reaches 100 percent. A planned restart does not require another code. The controller then opens **Remote screen** automatically.

The agent uses TCP 45832 and UDP 45833 on the local subnet. For private internet sessions, the relay is used for discovery and pairing, then a reachable LAN or public TCP 45832 endpoint is preferred for the rest of the session; the relay remains the fallback. No router configuration, port forwarding, or Internet service is needed when the relay is used.

Launching Remote Debugger again brings the existing desktop window forward. It does not open another instance or replace the current connection. This also restores a window hidden in the system tray. Use the role buttons inside that window to give or take control.

## During support

**Share clipboard** synchronizes new text copied while connected. Reconnecting, including after a reboot, starts a fresh baseline; text copied before or during a disconnection is not replayed. Turn the checkbox off to pause sharing.

Drag files or folders between Explorer and the **Files** pane, or over an Explorer folder or desktop visible in **Remote screen**. Copies preserve sources and empty folders. The transfer window shows progress and verification; use **Cancel** and **Resume** for queued uploads, or drag the same selection again to resume a download. Existing destination files require replacement confirmation. Virtual Explorer drags currently support up to 10,000 entries and relative paths shorter than 260 characters; filesystem links are rejected.

**Restart PC** explains the expected return before confirmation. Eligible local Windows accounts can use their account password for one automatic sign-in after that restart. Otherwise someone must sign in on the receiving PC. The controller waits up to one hour, shows confirmed stages and a countdown, and lets you stop waiting at any time. **Shut down PC** also confirms the named PC and provides a short cancellation window. Windows can wait for applications with unsaved work; loss of connection alone does not confirm physical power-off.

Incident reports and relevant logs are created automatically and retained locally for 30 days. Codex can read the controller's reports after disconnection when you request an investigation. Clipboard text and sign-in passwords are excluded. There are no scheduled follow-ups.

The header shows connection state. **LIVE** appears only while fresh frames arrive. The viewer enables mouse and keyboard by default; click inside it to direct input to the remote desktop. Moving focus away returns keyboard input to the local PC. Pause, lost connection, target changes, and termination release held input. The monitor selector changes the viewed desktop. Secure-desktop prompts and Ctrl+Alt+Delete remain Windows-controlled.

When the authenticated client runs the same build as the controller, the update button is disabled and reads **✓ Client up to date**. An enrolled admin PC can install strictly newer releases. In private support, **Update all devices** updates online older clients and shows each device's version, status, bar and ETA. Busy or offline devices can be retried later. A newer device version blocks an older controller from starting the batch.

**Processes** loads CPU, memory and process measurements after connection. Click a column header to sort; click again to reverse direction. Unavailable values remain unavailable instead of appearing as zero. CPU values represent total logical-processor capacity. **Files** loads the remote workspace and storage information automatically, supports typed sorting, and keeps the current directory separate from the selected file.

The close button on either PC hides the application in the system tray and keeps the session running. Double-click its icon or use **Open** to restore it. The controller resumes live viewing automatically if it was viewing before being hidden. The tray also offers **End support** and **Quit**. Ending support on either PC immediately revokes access, cancels work, closes administrator maintenance, and releases the temporary sleep request. The receiving PC displays a ten-minute automatic-exit countdown, which continues in the tray. Choose **New support session** before it expires to cancel the exit and get a new code. The controlling PC remains open. A lost controller first starts a separate ten-minute reconnect grace period; reconnecting cancels that grace period. Saved Windows power settings are never modified.

All three tables (available PCs, processes and files) save column widths and order independently. Drag headers to reorder them, drag their edges to resize them, or double-click an edge to fit its contents. The layout survives closing/reopening the program and adapts to Windows display scaling.

The mouse and keyboard checkbox is your preference. Temporary input errors do not uncheck it: the separate input status explains whether control is waiting for focus, a fresh frame, or reconnection. Pending mouse movements are combined so a slow connection does not build up a long pointer backlog.

## Files and diagnostics

In **Files**, enter a full remote folder path and click **Go** (or press Enter), or double-click folders in the list. **Upload file** sends a local file into the open remote folder; **Upload folder** sends a local folder's contents there, preserving subfolders. Select a remote file and click **Download** to choose where to save it on this PC. The transfer area shows percentage, bytes, speed and ETA (for the whole selection when uploading a folder), followed by verification. **Cancel** pauses the transfer; repeat it to resume. Files must be accessible to the receiving Windows user. Transfers verify the complete SHA-256 before replacing their destination.

The assisted PC also shows progress on **Give control** during application updates and incoming or outgoing files. File transfers identify the current filename and show speed and ETA; completed incoming files are marked as verified.

**Diagnostics** exposes structured operations, bounded command output, process launch/stop/restart, event and service information, native debugger attachment, minidumps, and intervention history. `maintenance.session` runs through the administrator broker for the paired session when **Admin maintenance** is enabled. `maintenance.status` reports actual availability. No one-hour maintenance button or recurring elevation dialog is required after setup.

See [the CLI reference](CLI.md) for automation and [the validation record](VALIDATION.md) for the behavior actually exercised in isolated Release tests.
