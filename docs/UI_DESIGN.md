# Support interface specification

This specification is the visual and behavioral target for the native Windows application; the SVGs are composition references, not screenshots of working software.

## Visual thesis

A calm technical workspace: porcelain surfaces, a deep navy navigation rail, a single teal action color, and strong type hierarchy. The remote desktop is the dominant working surface. Status is small, persistent, and literal.

## Tokens and geometry

- Default window 1280 x 860; minimum 1060 x 720. Respect Windows DPI scaling at 100, 125, 150 and 200 percent. Use layout containers, not fixed device-pixel positions.
- Rail 216 logical px; main header 96 px; main padding 28 px (20 px around the viewer); footer/status strip 40 px. Spacing scale 4, 8, 12, 16, 24, 32. Keep geometry stable across states.
- Canvas #F7F9FA; working surfaces #FFFFFF; rail #18212B; rail secondary #A8BAC2; primary text #183039; secondary text #637780; divider #DFE6EA; selected navigation #273C46.
- Primary action #087F83 with white text; hover #076C70. Connected background #E3F3E9 and text #206B45. Warning background #FFF2DB and text #8B5E12. Destructive text #B83D49, pale background #FFF0F1, solid destructive button only in its active hover/pressed state.
- Segoe UI throughout: title 22 semibold, section 15 semibold, body/control 10.5-11, metadata 9.5. Pairing code Consolas 42 bold. Table headers 10 semibold; rows 10.5. Do not substitute typography with rendered images.
- Buttons 38 px high, 14 px horizontal padding, subtle 5-6 px corners; icon buttons minimum 34 x 34. Inputs 40 px high, 1 px border, visible teal focus. Tables 36 px header and 34 px rows; alternate backgrounds only if needed for scanning.
- Use simple consistent line icons (16-18 px) with accessible text. No emoji, decorative gradients, oversized shadows, nested cards or marketing slogans. A single thin divider separates each working region.

## Shared shell

Rail: small RD square mark and Remote Debugger wordmark; separator; role buttons in this exact order: Donner le controle, Prendre le controle. French labels must use correct accents in the rendered UI. Selected role has a teal vertical indicator and navy-light fill. Under Prendre le controle, show Ecran distant, Processus, Fichiers, Diagnostics; keep Connexion accessible above these. Bottom rail shows local PC name and application version.

Header: left current screen title and one line of context, right one rounded status pill and the Terminer l'assistance button during a session. Termination is always reachable, including while requests are pending. The status pill includes text and a small dot; color alone never communicates state. Default disconnected state must not be green. Never derive connected status from a saved token.

Footer: concise actual task/status on left, measurement timestamp or frame statistics on right. Progress and errors use this strip or a compact inline banner; no modal for routine loading or retries.

Header and agent text share the same left content edge. Text rendering must not add font-size-dependent indentation. Heading and subtitle rows use measured text height, including wrapped lines, rather than fixed heights that clip ascenders or descenders.

## Agent: first view (agent-pairing.svg)

Header title Donner le controle, subline name of local computer. Pairing content in main canvas, width 620 logical px, aligned 64 px from left main edge and 74 px below header. Eyebrow CODE DE CONNEXION, title Partagez ce code, subline Saisissez-le sur le PC qui vous assiste.

Large six digit code (3+3 visual grouping permitted; copy returns exactly six ASCII digits), baseline aligned with small Copier button. Below: thin teal countdown bar, text Nouveau code dans 04:32; code expiry/rotation is authoritative from core, not a cosmetic timer. Preparing failures replace placeholder code with a clear actionable state, never an apparently usable code.

Under a horizontal divider, three compact rows with icon/dot, label, value: Reseau prive / Pret or Preparation..., Mise en veille / Active while waiting and Suspendue during authenticated support, Maintenance admin / Apres connexion. The maintenance row also has a compact **Enabled** checkbox; unchecking it closes and blocks the administrator broker for this PC until re-enabled. Show a once-needed setup notice inline when service is absent with Activer sur ce PC, and honest explanation Windows approval is once required. Automatic preparation may invoke initial setup only after this main window renders. No start-agent, open-pairing, revoke or manual session-admin buttons.

Agent after pairing: same footprint, replace code with connected controller name and session duration. Show screen/control/admin state with separate explicit values. During disconnection show Reconnexion en attente and remaining ten-minute lifetime. Keep Terminer l'assistance prominent. Closing either role hides its window in the tray, with a first-use notice and a tray termination action. Ending support revokes access and starts a visible ten-minute automatic-exit countdown on the assisted PC, including while hidden in the tray. Nouvelle assistance cancels the exit countdown; no new code or power hold is created until that action. The controller stays open.

## Controller: connection view

Header Prendre le controle; subline Choisissez un PC puis saisissez son code. Main area two columns: available PC list 360 px on left, selected-machine connection form flexible right, separated by a vertical divider. List rows show machine name primary, IP secondary, availability trailing; scan progress appears above list, Actualiser compact right. Empty state offers manual IP entry. Never list self.

Selected machine name, IP, one 6-digit field (160 px, Consolas 20), teal Connecter button. Enter in the field triggers connection once. Focus code when a peer is selected. No fingerprint field or verification checkbox. Advanced technical identity may be inspectable under Diagnostics without being a required step.

During pairing/sync keep selected target fixed, disable duplicate submissions, show Appairage... / Synchronisation de l'agent... / Reconnexion... in the form. Do not label Live until a fresh frame is displayed and hashes match. Selecting another target cancels pending work and clears its input queue/state.

During an update, show a thin transfer bar and an explicit percentage plus received/total MiB. The controller uses acknowledged bytes, and the agent uses bytes actually written. The agent reuses the code-countdown area after pairing. At 100 percent, label verification and restart/reconnection separately; transfer completion must not imply a live session. The terminate action remains available during synchronization.

## Controller: live view (controller-live.svg)

Header remote PC name and IP, Connected pill, terminate button. Toolbar under header: Ecran label with monitor selector, checked Controle souris et clavier toggle, small Pause/Reprendre viewing action. Main viewport fills remaining space with #142630 letterbox; no giant padding or redundant surrounding cards. Small EN DIRECT badge sits within the viewer top-left only while frames are current. An interruption overlay must clearly distinguish the last frozen frame.

Input defaults enabled but events only forward with viewer focus and valid frame geometry. The checkbox represents user preference and never changes on a transport error. A separate input status explains waiting or recovery. Coalesce adjacent mouse moves, preserve click/key order, and serialize release after input already in flight. Focus loss, pause, tab switch, update, disconnect, or termination releases keys/buttons. Footer frame rate, bandwidth, and latency uses actual values.

Both roles hide to tray on X, including repeated close messages. Explicit Quit and Windows shutdown exit. Keep the heartbeat alive while hidden, pause unnecessary frame presentation, and resume viewing on restore if it was previously running. Tray menu Ouvrir / Terminer l'assistance / Quitter. Restoring retains the selected tab and maximized state. Exit ends controller activity and revokes the remote session if reachable; otherwise its heartbeat timeout handles the grace period.

## Processes and files

Processes header + status/action remain unchanged. Top one horizontal summary row CPU / RAM / Processus / measured-at, each label/value plain typography separated by whitespace, no dashboard cards. Below full-width sortable table: PID 80, Application flexible, CPU % 100, RAM Mio 110, Reponse 100, Fenetre flexible. Numeric cells right-aligned, name cells left. Selected row subtle teal fill. Sort arrow visible, stable numeric sorting with unavailable values consistently last, preserve selection/PID and sort after refresh. CPU and RAM reflect real measurements; startup loading must not appear as zero.

Files: compact path breadcrumb/input + Parent/Actualiser; below full table Nom / Type / Taille / Modifie with typed sorting and folders grouped where appropriate. Actions Televerser un fichier, Televerser un dossier, Telecharger are a compact toolbar. Separate current directory from selected file so selecting a file does not overwrite the browsing path. Initial remote workspace listing loads upon connection. Inaccessible/empty/loading states are distinct.

Diagnostics: retain all existing operations, JSON arguments/results, deployment verification, cancellation and advanced troubleshooting. Label the arguments and result editors, load the selected operation's template, and enable PID only for operations that accept it. Identity comes from the current authenticated connection. Show validation errors, execution, cancellation and remote failures inline. Do not make raw JSON the default landing page.

Every workspace owns its footer: connection and selected PC, frame status, resource measurements, file count and directory, or diagnostic state and selected action. Saved credentials never enable remote actions. Disable pairing while a session exists, disable unavailable remote actions before connecting, and clear stale results and selections after termination. The minimum window scales with Windows DPI but is capped to the display's working area; smaller agent workspaces scroll.

Mixed-control rows center labels, fields and buttons vertically using content-sized layout. Resource values receive their full measured font height, with the volumes line below them. Each PC, process and file table independently remembers column widths and order across restarts and DPI changes. Keep table headers usable during a session while preventing a change of connected PC through row selection.

## Accessibility and interaction

The shared sidebar includes a compact Language selector above the local PC/version metadata. Options are System default, English, Français and Español; language names remain in their native spelling. Apply and save changes immediately without restarting, disconnecting, changing the selected tab or resetting editors/table state. The selector remains available during support. Localize its caption and System default option, and preserve stable Automation IDs `languageSelector` and `languageLabel`.

Preserve or deliberately document UI Automation names/ids for acceptance. New stable ids: pairCode, pair, peers, host, connectionStatus, agentPairCode, pairingCountdown, agentState, terminateSession, processList, remoteFiles, remoteScreen, streamStatus, refreshResources, browseFiles. Expose text values via native controls; do not paint all content into one inaccessible bitmap.

Use a short opacity/state transition only where native controls support it without complexity; no ornamental animation. Countdown bar updates smoothly enough to read, busy operations show a modest progress indicator. Honor Windows high-contrast/reduced-motion preferences. Keyboard tab order follows visual order. Validate no clipped headings/buttons/rows at minimum size and 200 percent DPI.

## Review gate

Root reviews actual Release screenshots of pairing, connection, live, process and files screens and error/reconnect state against this document. A compile or automation assertion alone does not prove visual completion. Any design deviations need a concrete reason and root review; do not invent an alternate layout.
