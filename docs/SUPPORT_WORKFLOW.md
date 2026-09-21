# Remote Debugger 0.5.0 support workflow

Agreed scope, 21 September 2026. Implementation and acceptance remain in progress.

## Requirements

- Clipboard: bidirectional text; only new events during an authenticated connection. Pause and discard pending changes on disconnect. Establish a fresh baseline after reconnect/reboot. End/expiry stops capture. Never include clipboard contents in reports.
- Reports: automatically record incidents and relevant operational history, deduplicate repeated faults, retain 30 days locally. No scheduled investigations or outbound reporting. Reports must remain usable after the target disconnects; investigation starts when the user requests it.
- Restart: controller preflight explains automatic desktop return versus manual sign-in. Optional one-use logon is limited to that restart with temporary protected credentials and cleanup. Resume the same authorized support session, with a one-hour controller countdown cancellable at any time. Cancellation stops reconnection and cannot undo an issued restart. Ready requires an authenticated session, fresh desktop and usable input.
- Shutdown: confirmation on the controller names the remote PC. Short cancellable countdown; normal Windows application shutdown, no implicit forced closure. A disconnected PC alone is not proof of power-off.
- Update progress: real byte percentages, smoothed measured transfer ETA, activity/elapsed for opaque stages, historical timing explicitly estimated. Finish stages on their actual completion event. Overall success requires the intended running executable and healthy agent. Fleet outcomes stay independent per PC.
- Viewer: adaptive 15–30 FPS on capable PCs/connections, lower under load and economical relay viewing. Prefer fresh frames and responsive input over a frame backlog.
- Drag and drop: copy files and folders both ways between local Explorer and both the Files pane and remote desktop viewer. Queue items, confirm overwrites, support cancellation/resume and verify integrity. Do not move/delete source files.

## Acceptance evidence required

- Clipboard events before connection, during an outage, after end, and from a prior boot must not be replayed. Connected changes work both ways without loops.
- Reports automatically capture unexpected faults but classify planned restart/shutdown/end correctly; old logs expire and clipboard/password contents are absent.
- Restart preflight matches guest configuration. One-time logon returns a desktop only once; another boot requires normal sign-in. Manual sign-in path, cancellation and one-hour expiry are verified.
- Shutdown confirmation and cancellation are verified in an isolated guest; issued shutdown uses the harness expected-power-off contract.
- Update steps never show elapsed-time-derived completion percentages; slow/stalled/resumed transfers have honest ETA. Completion is tied to remote verification.
- Viewer presents fresh frames above the old 5 FPS ceiling on a capable guest and backs off for slow capture/consumption without delaying input.
- Real Explorer drag/drop works for files and nested folders, both directions and both app surfaces, including conflicts, interruption/resume and matching hashes.

Application tests use the Hyper-V SYSTEM broker. Luna Max runs focused tests; the primary agent owns application and test code. Final deliverables use signed Release builds and installers, committed and pushed source, and a requirement-by-requirement evidence record.
