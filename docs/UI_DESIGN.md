# Support interface specification

Design owner: root agent. Implementation owner: Luna Max. This specification is the visual and behavioral target for the native Windows application; the SVGs are composition references, not screenshots of working software.

## Visual thesis

A calm technical workspace: porcelain surfaces, a deep navy navigation rail, a single teal action color, and strong type hierarchy. The remote desktop is the dominant working surface. Status is small, persistent, and literal.

## Tokens and geometry

- Default window 1280 x 860; minimum 1060 x 720. Respect Windows DPI scaling at 100, 125, 150 and 200 percent. Use layout containers, not fixed device-pixel positions.
- Rail 208 logical px; main header 84 px; main padding 28 px; footer/status strip 40 px. Spacing scale 4, 8, 12, 16, 24, 32. Keep geometry stable across states.
- Canvas #F6F8FA; working surfaces #FFFFFF; rail #142630; rail secondary #A8BAC2; primary text #183039; secondary text #637780; divider #DFE6EA; selected navigation #24444E.
- Primary action #087F83 with white text; hover #076C70. Connected background #E3F3E9 and text #206B45. Warning background #FFF2DB and text #8B5E12. Destructive text #B83D49, pale background #FFF0F1, solid destructive button only in its active hover/pressed state.
- Segoe UI throughout: title 22 semibold, section 15 semibold, body/control 10.5-11, metadata 9.5. Pairing code Consolas 42 bold. Table headers 10 semibold; rows 10.5. Do not substitute typography with rendered images.
- Buttons 38 px high, 14 px horizontal padding, subtle 5-6 px corners; icon buttons minimum 34 x 34. Inputs 40 px high, 1 px border, visible teal focus. Tables 36 px header and 34 px rows; alternate backgrounds only if needed for scanning.
- Use simple consistent line icons (16-18 px) with accessible text. No emoji, decorative gradients, oversized shadows, nested cards or marketing slogans. A single thin divider separates each working region.

## Shared shell

Rail: small RD square mark and Remote Debugger wordmark; separator; role buttons in this exact order: Donner le controle, Prendre le controle. French labels must use correct accents in the rendered UI. Selected role has a teal vertical indicator and navy-light fill. Under Prendre le controle, show Ecran distant, Processus, Fichiers, Diagnostics; keep Connexion accessible above these. Bottom rail shows local PC name and application version.

Header: left current screen title and one line of context, right one rounded status pill and the Terminer l'assistance button during a session. Termination is always reachable, including while requests are pending. The status pill includes text and a small dot; color alone never communicates state. Default disconnected state must not be green. Never derive connected status from a saved token.

Footer: concise actual task/status on left, measurement timestamp or frame statistics on right. Progress and errors use this strip or a compact inline banner; no modal for routine loading or retries.

## Agent: first view (agent-pairing.svg)

Header title Donner le controle, subline name of local computer. Pairing content in main canvas, width 620 logical px, aligned 64 px from left main edge and 74 px below header. Eyebrow CODE DE CONNEXION, title Partagez ce code, subline Saisissez-le sur le PC qui vous assiste.

Large six digit code (3+3 visual grouping permitted; copy returns exactly six ASCII digits), baseline aligned with small Copier button. Below: thin teal countdown bar, text Nouveau code dans 04:32; code expiry/rotation is authoritative from core, not a cosmetic timer. Preparing failures replace placeholder code with a clear actionable state, never an apparently usable code.

Under a horizontal divider, three compact rows with icon/dot, label, value: Reseau prive / Pret or Preparation..., Mise en veille / Suspendue, Maintenance admin / Apres connexion. Show a once-needed setup notice inline when service is absent with Activer sur ce PC, and honest explanation Windows approval is once required. Automatic preparation may invoke initial setup only after this main window renders. No start-agent, open-pairing, revoke or manual session-admin buttons.

Agent after pairing: same footprint, replace code with connected controller name and session duration. Show screen/control/admin state with separate explicit values. During disconnection show Reconnexion en attente and remaining ten-minute lifetime. Keep Terminer l'assistance prominent. Agent close exits; it does not silently hide the supported user's visibility.

## Controller: connection view

Header Prendre le controle; subline Choisissez un PC puis saisissez son code. Main area two columns: available PC list 360 px on left, selected-machine connection form flexible right, separated by a vertical divider. List rows show machine name primary, IP secondary, availability trailing; scan progress appears above list, Actualiser compact right. Empty state offers manual IP entry. Never list self.

Selected machine name, IP, one 6-digit field (160 px, Consolas 20), teal Connecter button. Enter in the field triggers connection once. Focus code when a peer is selected. No fingerprint field or verification checkbox. Advanced technical identity may be inspectable under Diagnostics without being a required step.

During pairing/sync keep selected target fixed, disable duplicate submissions, show Appairage... / Synchronisation de l'agent... / Reconnexion... in the form. Do not label Live until a fresh frame is displayed and hashes match. Selecting another target cancels pending work and clears its input queue/state.

## Controller: live view (controller-live.svg)

Header remote PC name and IP, Connected pill, terminate button. Toolbar under header: Ecran label with monitor selector, checked Controle souris et clavier toggle, small Pause/Reprendre viewing action. Main viewport fills remaining space with #142630 letterbox; no giant padding or redundant surrounding cards. Small EN DIRECT badge sits within the viewer top-left only while frames are current. An interruption overlay must clearly distinguish the last frozen frame.

Input defaults enabled but events only forward with viewer focus and valid frame geometry. Focus loss, pause, tab switch, update, disconnect, or termination releases keys/buttons. Footer frame rate, bandwidth, and latency uses actual values.

Controller X hides to tray; keep heartbeat alive. One first-use tray notice is acceptable. Tray menu Ouvrir / Terminer l'assistance / Quitter. Restoring retains the selected tab and ongoing session. Exit ends controller activity; if agent reachable it gets explicit disconnect and otherwise its heartbeat timeout handles the grace period.

## Processes and files

Processes header + status/action remain unchanged. Top one horizontal summary row CPU / RAM / Processus / measured-at, each label/value plain typography separated by whitespace, no dashboard cards. Below full-width sortable table: PID 80, Application flexible, CPU % 100, RAM Mio 110, Reponse 100, Fenetre flexible. Numeric cells right-aligned, name cells left. Selected row subtle teal fill. Sort arrow visible, stable numeric sorting with unavailable values consistently last, preserve selection/PID and sort after refresh. CPU and RAM reflect real measurements; startup loading must not appear as zero.

Files: compact path breadcrumb/input + Parent/Actualiser; below full table Nom / Type / Taille / Modifie with typed sorting and folders grouped where appropriate. Actions Televerser un fichier, Televerser un dossier, Telecharger are a compact toolbar. Separate current directory from selected file so selecting a file does not overwrite the browsing path. Initial remote workspace listing loads upon connection. Inaccessible/empty/loading states are distinct.

Diagnostics: retain all existing operations, JSON arguments/results, deployment verification, cancellation and advanced troubleshooting; place them in an explicit technical workspace with labels. Do not make raw JSON the default landing page.

## Accessibility and interaction

Preserve or deliberately document UI Automation names/ids for acceptance. New stable ids: pairCode, pair, peers, host, connectionStatus, agentPairCode, pairingCountdown, agentState, terminateSession, processList, remoteFiles, remoteScreen, streamStatus, refreshResources, browseFiles. Expose text values via native controls; do not paint all content into one inaccessible bitmap.

Use a short opacity/state transition only where native controls support it without complexity; no ornamental animation. Countdown bar updates smoothly enough to read, busy operations show a modest progress indicator. Honor Windows high-contrast/reduced-motion preferences. Keyboard tab order follows visual order. Validate no clipped headings/buttons/rows at minimum size and 200 percent DPI.

## Review gate

Root reviews actual Release screenshots of pairing, connection, live, process and files screens and error/reconnect state against this document. A compile or automation assertion alone does not prove visual completion. Any design deviations need a concrete reason and root review; do not invent an alternate layout.
