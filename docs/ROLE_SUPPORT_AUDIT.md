# Controller / agent requirements and diagnostic audit

Agreed with Patrice on 2026-09-25. This document is the authoritative handoff for this investigation. Later user corrections supersede earlier conversation, existing source behavior, and older documentation.

## Scope and stopping point

The initial investigation documented requirements and failures before proposing changes. Patrice subsequently approved implementation on 2026-09-25: **“fix it all, including the aforementioned installer issue.”** The audit results remain the historical 0.5.21 baseline; correction verification is recorded separately in [ROLE_SUPPORT_FIX_RESULTS.md](ROLE_SUPPORT_FIX_RESULTS.md). Parent agent writes code; latest Luna Max agents run tests. Use the Hyper-V SYSTEM broker for executable, installer, and native UI testing. No application-under-test execution on the physical host. No real family PC or production-network experiments.

The earlier, separate authorization allowed the Hyper-V Harness project, using Astra Max, to implement and qualify the missing I01/I03/I04 test capabilities. Those capabilities were deployed before the product correction tests.

## Agreed requirements

1. Agent and controller are authorization roles, not interchangeable navigation views.
2. A fresh installation is an agent unless the setup controller/admin-PC checkbox is selected **and** the specific controller password successfully unlocks a valid controller credential. Checkbox selection, Windows administrator rights, UAC consent, and private-network credentials alone do not confer controller authority.
3. Existing valid controller enrollment may survive reinstallation without entering the controller password again. An agent stays an agent when installed or run with Windows elevation.
4. There is **one** incoming permission: **Support enabled**, including administrator support. There is no separate ordinary-support versus privileged-maintenance permission in the required product model.
5. On agents, **Support enabled is visible, ON, and greyed out**. It is always on and cannot be changed. **Take control is visible and greyed out**.
6. On controllers, **Support enabled is editable and OFF by default**. Take control remains available independently of the incoming-support setting.
7. Agents cannot list/discover other PCs, initiate a support session, control another PC, or update any other PC. This includes other agents and controllers, regardless of versions and target support setting.
8. An authorized controller can support an agent. It can support another controller only while that target controller's Support enabled setting is ON.
9. These rules cover screen viewing, input, files, processes, commands, administrator operations, and remotely initiated updates. Enforce them in backend/protocol authorization as well as UI controls. Manual addresses, support IDs, pairing codes, private-network secrets, CLI/API entry points, command-line flags, and saved sessions must not bypass the role boundary.
10. Turning incoming support OFF on a controller must reject incoming access while preserving its outgoing controller capabilities. Test active-session termination and saved-session reconnection against this condition.
11. No computer may be downgraded. Test both remote update and local installation routes. Different binaries with the same release version are not valid remote upgrades; an identical installed binary needs no replacement. Distinguish recovery of a failed, uncommitted update from deliberately installing an older release; do not silently decide recovery semantics if that becomes relevant.
12. An agent can never update a controller, including to a newer version. Version equality or ordering never grants controller authority.
13. Normal setup launch must request the necessary Windows elevation automatically **before protected file writes, service installation, or helper provisioning** and complete helper setup successfully once consent/credentials are supplied. Manual Run as administrator must not be required as a recovery step. Cancelled elevation must not produce a false ready state. Verify the actual elevation order in the packaged installer, not only its source declaration.
14. Preserve role and the controller's explicit support setting across restart, reboot, reinstall, and upgrade. Review stale/corrupt settings and old credentials without silently granting authority.
15. Failure UI must accurately show the terminal failure and recovery action; no simultaneous preparing/reconnecting/in-progress state after failure.

## Role matrix

| Initiator | Receiver | Receiver support state | Required result |
|---|---|---|---|
| Agent | Agent | Always ON | Deny |
| Agent | Controller | OFF | Deny |
| Agent | Controller | ON | Deny |
| Authorized controller | Agent | Always ON | Allow authorized support, including administrator support |
| Authorized controller | Controller | OFF | Deny |
| Authorized controller | Controller | ON | Allow authorized support, including administrator support |

Agents must not obtain a fleet listing. Controller target listings should identify eligible support targets; a controller with support OFF must not be usable as a target. Do not infer that OS-level network address visibility can be eliminated.

## Reported incidents

- A family agent displayed Take control, discovered the owner's controller, attempted a connection, then attempted synchronization using an older version. Downgrade was rejected. The supplied screenshot displayed version 0.5.20 and contradictory in-progress/failure states.
- A second family PC, freshly installed as a **0.5.21 agent**, with **no controller password ever entered**, listed, connected to, and controlled the owner's controller while its incoming support was OFF.
- That 0.5.21 setup first failed with an approximate helper-installation/provisioning error. Running setup explicitly as administrator succeeded. The exact error text is unavailable.

The primary regression is a fresh agent with no controller authority, the same version as a controller target, and target support OFF. A version mismatch must not mask the role and incoming-support failures.

## Test inventory

Statuses and evidence belong in ROLE_SUPPORT_AUDIT_RESULTS.md. Never turn a source observation, fixture result, successful harness run, or missing assertion into a production pass.

| ID | Scenario / required observation |
|---|---|
| E01 | Fresh setup, controller checkbox unchecked: agent, no controller credential |
| E02 | Checkbox checked but no password: no controller authority |
| E03 | Wrong controller password: reject without partial enrollment |
| E04 | Cancel controller password: no authority |
| E05 | Correct controller password: enroll, show controller UI, incoming support defaults OFF |
| E06 | Reinstall agent/controller: preserve intended enrollment, no elevation-driven promotion |
| P01 | Agent Support enabled visible ON/disabled; Take control visible/disabled |
| P02 | Fresh controller Support enabled OFF/editable; Take control enabled |
| C01 | Agent to agent: reject pairing and every remote operation |
| C02 | Agent to controller OFF: reject pairing and every remote operation |
| C03 | Agent to controller ON: reject pairing and every remote operation |
| C04 | Controller to agent: screen/input/files/processes/commands/admin support usable |
| C05 | Controller to controller OFF: reject pairing and every remote operation |
| C06 | Controller to controller ON: all authorized support operations usable |
| B01 | Agent Take control UI / --controller / CLI / API / manual address cannot bypass role |
| B02 | Agent cannot obtain LAN fleet listing; authorized controller positive discovery control |
| B03 | Agent cannot obtain relay fleet listing; authorized controller positive relay control |
| B04 | Pairing code or shared private-network access without controller credential grants no control |
| B05 | Saved outgoing grants/restart cannot restore agent control; revoked authority is not silently reused |
| S01 | Controller toggles support OFF during incoming session: stop access and reject reconnect |
| S02 | Controller support OFF persists across process restart, reboot, reinstall, and upgrade |
| S03 | Agent ends current support: remains an always-enabled agent available for later support |
| S04 | Legacy/missing/corrupt settings do not create controller authority or enable incoming controller support |
| U01 | Authorized newer controller upgrades an older supported agent; verify installed bytes and reconnect |
| U02 | Identical release: support works without replacement |
| U03 | Older controller versus newer target: refuse downgrade before transfer/replacement |
| U04 | Same release version, different executable: refuse replacement |
| U05 | Agent-originated update: reject for older/equal/newer candidate and both target roles |
| U06 | Older standalone installer against newer installation: refuse downgrade |
| U07 | Same-version local reinstall: preserve role/settings/helper without unauthorized promotion |
| U08 | Controller with support OFF rejects remotely initiated update even from another controller |
| I01 | Exact 0.5.21 setup, normal unelevated launch: automatic UAC and successful helper setup |
| I02 | Explicit elevated setup comparison: same installed role/helper outcome; distinguish interactive elevation from broker guest setup |
| I03 | Standard Windows user with admin credentials: correct original-user ownership/configuration |
| I04 | Decline UAC: cancellation is clear; no false complete/ready state |
| I05 | Existing installation/helper refresh: success with preserved role and settings |
| F01 | Denied connection/update has one truthful terminal state and usable recovery UI |

Repeat representative denial cases with older/equal/newer initiating binaries. Run direct LAN and relay coverage separately; loopback only proves application authorization, not discovery or relay policy. Each allowed row needs a positive control. Administrator operations require a provisioned guest helper; an unprovisioned failure is not authorization proof.

## Artifact identity and prior evidence

- Original Release: `artifacts/release/RemoteDebugger.exe`, 0.5.21, SHA-256 `95199EA784F4E5D983F770E06D5802F22AD8470104CDDBA0C440E2344D919511`, source commit `0afabcbdd7ee2b12f6600b3f571de38b0dbe9f04`.
- Exact private setup: `artifacts/installer/RemoteDebugger-0.5.21-Private-Setup.exe`, SHA-256 `7F7ABADADA531BC1E547850F855BD53DF1C90C7D5F991F6BFE57A5382F17F353`.
- One installer request reached a harness prompt limitation: `executable-test-20260924T224746719Z-0f639040`, `The exact application started before the requested startup UAC prompt was observed.` No installer UI/log/helper outcome was captured. VM off, child deleted, no warnings. This is **blocked coverage**, not a product pass/fail. Receipt: `work/role-permission-audit/installer/attempt-01-result.json`.
- A task-local same-source Release fixture exists at `work/role-permission-audit/test-authority-release`. It uses only the repository's existing disposable test update-authority constant. It is not the shipped binary, and must never be published or installed on real PCs. Label fixture evidence separately.

## Source findings to verify, not fixes

- `MainForm.SelectRole` treats role navigation as presentation only; normal discovery/pairing paths do not require enrolled controller authority.
- `AdminMaintenancePreference` defaults ON and controls privileged maintenance separately from pairing. This does not implement the agreed one-permission model.
- Controller checkbox/password enrollment and cryptographic remote-update authority checks are present.
- Remote update begin/stage/commit paths require strictly newer releases.
- Standalone setup installs the executable with `ignoreversion`; no installed-version downgrade rejection was found.

## Corrective plan deliverable

For each confirmed failure: record reproduction, scope/artifact, expected vs actual, root-cause evidence, correction, affected interfaces/compatibility, and regression tests. Prioritize role authorization and incoming-support enforcement before presentation symptoms. List harness/capability work separately from product work. Product implementation was authorized after the baseline review.
