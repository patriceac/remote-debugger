# Getting started

The app automatically uses the Windows display language: French, English or Spanish, with English for other languages. Use **Language** at the bottom of the sidebar to select **System default**, **English**, **Français** or **Español**. The choice applies immediately, keeps the current session and is remembered next time. Each PC selects its own language. The instructions below use the English labels. Use the same signed Release on both Windows x64 PCs.

1. On each PC, run the per-user installer for a Start menu entry, or copy and launch the portable `RemoteDebugger.exe`. The installer itself needs no elevation. Launch Remote Debugger on the receiving PC. **Give control** is the default: the agent starts automatically and displays a six-digit code and its remaining validity. Leading zeroes are significant. The code changes every five minutes until paired.
2. On first use, **Enable on this PC** performs the one-time Windows administrator setup. It installs the protected support application and a local broker, enrolls the publisher, and relaunches the managed copy. Thereafter Private-network authorization and administrator maintenance are automatic. The application does not change a Public network to Private.
3. On the controlling PC choose **Take control**. Nearby PCs appear automatically. Select the receiving PC, or enter its IP address if discovery is unavailable.
4. Enter the receiving PC's code and press Enter or **Connect**. Pairing authenticates the encrypted connection using the code, and subsequent connections pin that identity.
5. Wait for binary synchronization. If necessary, the agent silently receives and restarts into the exact signed executable running on the controller. Both PCs show actual transfer bytes and percentage; verification and restart are separate stages after the transfer reaches 100 percent. A planned restart does not require another code. The controller then opens **Remote screen** automatically.

The agent uses TCP 45832 and UDP 45833 on the local subnet. No router configuration, port forwarding, or Internet service is needed.

Launching Remote Debugger again brings the existing desktop window forward. It does not open another instance or replace the current connection. This also restores a window hidden in the system tray. Use the role buttons inside that window to give or take control.

## During support

The header shows connection state. **LIVE** appears only while fresh frames arrive. The viewer enables mouse and keyboard by default; click inside it to direct input to the remote desktop. Moving focus away returns keyboard input to the local PC. Pause, lost connection, target changes, and termination release held input. The monitor selector changes the viewed desktop. Secure-desktop prompts and Ctrl+Alt+Delete remain Windows-controlled.

**Processes** loads CPU, memory and process measurements after connection. Click a column header to sort; click again to reverse direction. Unavailable values remain unavailable instead of appearing as zero. CPU values represent total logical-processor capacity. **Files** loads the remote workspace and storage information automatically, supports typed sorting, and keeps the current directory separate from the selected file.

The close button on either PC hides the application in the system tray and keeps the session running. Double-click its icon or use **Open** to restore it. The controller resumes live viewing automatically if it was viewing before being hidden. The tray also offers **End support** and **Quit**. Ending support on either PC immediately revokes access, cancels work, closes administrator maintenance, and releases the temporary sleep request. The receiving PC displays a ten-minute automatic-exit countdown, which continues in the tray. Choose **New support session** before it expires to cancel the exit and get a new code. The controlling PC remains open. A lost controller first starts a separate ten-minute reconnect grace period; reconnecting cancels that grace period. Saved Windows power settings are never modified.

All three tables (available PCs, processes and files) save column widths and order independently. Drag headers to reorder them, drag their edges to resize them, or double-click an edge to fit its contents. The layout survives closing/reopening the program and adapts to Windows display scaling.

The mouse and keyboard checkbox is your preference. Temporary input errors do not uncheck it: the separate input status explains whether control is waiting for focus, a fresh frame, or reconnection. Pending mouse movements are combined so a slow connection does not build up a long pointer backlog.

## Files and diagnostics

Use versioned upload destinations such as `deployments/MyApp/1.2.3`. Upload writes stay beneath the remote workspace and use resumable chunks plus complete SHA-256 verification. File reads and downloads may use paths accessible to the receiving Windows user. A changed download fails verification instead of replacing the destination with unverified data.

**Diagnostics** exposes structured operations, bounded command output, process launch/stop/restart, event and service information, native debugger attachment, minidumps, and intervention history. `maintenance.session` runs through the administrator broker for the paired session. `maintenance.status` reports actual availability. No one-hour maintenance button or recurring elevation dialog is required after setup.

See [the CLI reference](CLI.md) for automation and [the validation record](VALIDATION.md) for the behavior actually exercised in isolated Release tests.
