# Getting started

The interface uses French labels. Use the same signed Release on both Windows x64 PCs.

1. Launch Remote Debugger on the receiving PC. **Donner le contrôle** is the default: the agent starts automatically and displays a six-digit code and its remaining validity. Leading zeroes are significant. The code changes every five minutes until paired.
2. On first use, **Activer sur ce PC** performs the one-time Windows administrator setup. It installs the protected support application and a local broker, enrolls the publisher, and relaunches the managed copy. Thereafter Private-network authorization and administrator maintenance are automatic. The application does not change a Public network to Private.
3. On the controlling PC choose **Prendre le contrôle**. Nearby PCs appear automatically. Select the receiving PC, or enter its IP address if discovery is unavailable.
4. Enter the receiving PC's code and press Enter or **Connecter**. Pairing authenticates the encrypted connection using the code, and subsequent connections pin that identity.
5. Wait for binary synchronization. If necessary, the agent silently receives and restarts into the exact signed executable running on the controller. Both PCs show actual transfer bytes and percentage; verification and restart are separate stages after the transfer reaches 100 percent. A planned restart does not require another code. The controller then opens **Écran distant** automatically.

The agent uses TCP 45832 and UDP 45833 on the local subnet. No router configuration, port forwarding, or Internet service is needed.

## During support

The header shows connection state. **EN DIRECT** appears only while fresh frames arrive. The viewer enables mouse and keyboard by default; click inside it to direct input to the remote desktop. Moving focus away returns keyboard input to the local PC. Pause, lost connection, target changes, and termination release held input. The monitor selector changes the viewed desktop. Secure-desktop prompts and Ctrl+Alt+Delete remain Windows-controlled.

**Processus** loads CPU, memory and process measurements after connection. Click a column header to sort; click again to reverse direction. Unavailable values remain unavailable instead of appearing as zero. CPU values represent total logical-processor capacity. **Fichiers** loads the remote workspace and storage information automatically, supports typed sorting, and keeps the current directory separate from the selected file.

The controller's close button hides it to the system tray and keeps the session running. Its tray menu offers Open, End support, and Exit. **Terminer l’assistance** is the explicit session-ending action on both PCs. Closing the receiving agent ends it. A lost controller starts a ten-minute reconnect countdown; reconnecting cancels the countdown. Expiry ends the agent, cancels support work, closes administrator maintenance, and releases the temporary sleep request. Saved Windows power settings are never modified.

## Files and diagnostics

Use versioned upload destinations such as `deployments/MyApp/1.2.3`. Upload writes stay beneath the remote workspace and use resumable chunks plus complete SHA-256 verification. File reads and downloads may use paths accessible to the receiving Windows user. A changed download fails verification instead of replacing the destination with unverified data.

**Diagnostics** exposes structured operations, bounded command output, process launch/stop/restart, event and service information, native debugger attachment, minidumps, and intervention history. `maintenance.session` runs through the administrator broker for the paired session. `maintenance.status` reports actual availability. No one-hour maintenance button or recurring elevation dialog is required after setup.

See [the CLI reference](CLI.md) for automation and [the validation record](VALIDATION.md) for the behavior actually exercised in isolated Release tests.
