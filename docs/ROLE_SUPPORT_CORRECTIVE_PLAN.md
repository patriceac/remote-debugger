# Proposed corrective plan: controller authority and receiving support

2026-09-25. **For Patrice's review. No product fix is authorized or implemented.** Requirements: [ROLE_SUPPORT_AUDIT.md](ROLE_SUPPORT_AUDIT.md). Evidence and coverage limits: [ROLE_SUPPORT_AUDIT_RESULTS.md](ROLE_SUPPORT_AUDIT_RESULTS.md).

## What is established

The exact .21 installer checkbox starts unchecked. Selecting it opens the controller-password prompt, and cancelling without entering a password leaves the installed computer an agent with no controller key. This confirms the setup/password gate; it does not prevent the separate support authorization failures below.

The password-protected controller credential exists, but current ordinary pairing does not require it. In the original 0.5.21 executable an unenrolled agent can pair and perform support operations using a pairing code or disposable shared private-network secret, including through the actual packaged MCP server. Removing fixture controller authority and restarting its source profile still leaves a saved grant usable through CLI and MCP. Disposable-controller fixture tests also accept actual text input with the target's current maintenance switch OFF. Current UI roles are presentation modes; the maintenance preference is separate from general session authorization. This combination explains a path consistent with the reported incidents without requiring accidental controller enrollment. It does not establish the exact historical state of either family PC.

The existing remote-update proof and strict-newer policy reject agent callers and older/equal candidates. Full authorized forward update, reconnect and identical-byte no-op worked in the disposable fixture. However, a controller with its receiving switch Off accepted a full newer update and subsequent support from another controller; its saved Off value survived. The reported automatic-elevation/helper failure remains unexplained: exact elevated installation and agent reinstall both produced an unenrolled agent and a Ready, identity-verified helper, while normal-launch UAC coverage is blocked by the harness bootstrap contract. A separate same-source fixture provisioning run failed with SCM 1053 despite elevation; preserve that receipt while investigating service startup readiness.

The exact standalone .21 installer also replaced a preinstalled synthetic .22 with the actual shipped .21 binary and exited zero. Local downgrade rejection is therefore a confirmed required correction, independent of the remote updater's working version checks.

## Proposed changes, in order

| Priority | Change to review | Affected boundaries | Required proof before release |
|---|---|---|---|
| P0 | Require a valid enrolled controller credential to initiate support. Bind a fresh controller proof to the receiving PC and session; reject unproven callers before establishing a grant or attempting synchronization. Reuse existing cryptographic authority primitives where suitable. | Pairing, manual IP/code/private-secret paths, CLI, GUI, connected API, saved/resumed sessions. A disabled button alone is insufficient. | C01-C06, B01/B04/B05: all forbidden rows reject at the backend, with allowed rows succeeding. Network passwords, pairing codes, Windows elevation and matching versions confer no controller authority. |
| P0 | Implement one receiving permission. Agents always have Support enabled ON; controllers default OFF and explicitly opt in. Check it on every incoming support/update entry point and enforce revocation on active grants. | LAN and relay admission, admin update channel, stream/input/files/commands, helper sessions, reconnect tickets. | C02/C05, S01-S04, U08: controller OFF denies all incoming operations, including updates; switching OFF cancels access and blocks reused grants. Outgoing support still works. |
| P0 | Restrict computer listings to proven controllers. A shared network bearer secret identifies network membership, not permission to control its members. | LAN discovery requester/response policy, relay directory authorization, GUI discovery and CLI discovery. | B02/B03: agent gets no fleet listing through direct APIs; authorized controller positive controls succeed. No legacy fallback restores agent privileges. |
| P1 | Make the UI reflect the enforced model: visible disabled Take control and ON/disabled Support enabled on agents; editable OFF-default Support enabled on controllers. Eliminate the separate permission meaning of Admin maintenance. | Role navigation, settings, localization, first launch, preferences/migration. | E01-E06, P01/P02, S04: UIA and screenshots match the agreed roles; missing/corrupt/legacy controller settings fail closed. |
| P1 | Reject local installer downgrades before stopping services or overwriting files. Retain remote proof and strict-newer checks at receiver and privileged stage/commit boundaries. | Interactive/silent setup, repair/reinstall, remote update, helper refresh. | U01-U07, E06/S02/I05: older setup cannot replace newer bytes; same-version reinstall preserves role/settings; forward update verifies bytes and reconnects. |
| P1 | Verify automatic installer elevation before any protected change, preserving the original user's configuration while provisioning the helper. Add clear stage-specific diagnostics if setup fails. Change implementation only once the failing path is established. | Inno bootstrap/token handoff, original-user setup operations, protected helper/service provisioning. | I01-I05: normal launch with consent succeeds; standard-user credentials act on the correct profile; UAC decline is clear and leaves no false-ready installation; explicit-elevation comparison matches. |
| P1 | End failed connection/update attempts in one terminal UI state. Stop stale progress/timers and expose the actual failure and recovery action on both participating PCs. | Synchronization exception handling, retained sessions, update progress rendering and reconnect status. | F01, U04's receiver progress observation and the remote-end footer in S03: reproduce refusal/session end and verify one accurate terminal state with usable recovery. |

## Compatibility and preservation

- Existing legitimate enrollment remains valid. Reinstalling or elevating an agent must never create controller authority.
- Controller proof supplements existing network-membership checks; it must not weaken those checks or expose credentials in listings or diagnostic logs.
- Older clients that cannot present controller authorization must be denied clearly. Version mismatch must not authorize an agent or trigger a downgrade attempt.
- A controller's explicit support preference must survive process restart, reboot, reinstall and forward upgrade. Legacy absence/corruption must not opt a controller into receiving support.
- The old maintenance switch has different semantics and defaults On. Do not silently interpret that old default as consent to the new unified receiving permission; agree a fail-closed migration during review.
- Keep the existing strictly-newer remote-update checks and distinguish failed uncommitted-update recovery from installing an older release. If recovery semantics conflict with the agreed rule, bring that concrete case to review.
- Credential removal with an already issued grant currently leaves access usable. The final grant design must state and test how local role revocation invalidates saved/resumed access.

## Source boundaries for implementation review

| Finding | Existing source boundary | Smallest coherent correction to agree |
|---|---|---|
| Navigation is not authority | `src/RemoteDebugger/MainForm.cs` (`SelectRole`); `Program.cs` (`discover`, `pair`); `Network.cs` (`pair.v2`) | Keep presentation checks, but make the receiver verify controller authority before issuing support grants. Route GUI/CLI/API through that same authorization. |
| Incoming Off leaves ordinary support and updates open | `src/RemoteDebugger.Core/AdminMaintenancePreference.cs`; `Network.cs`; `Network.Admin.cs`; `AgentUpdateService.cs` | Replace the separate maintenance permission with the one agreed receiving policy. Enforce it centrally for grants, existing requests, update admission, and resumed sessions. Do not rely on a label change. |
| Agents obtain a directory | `src/RemoteDebugger/Network.cs` (`Discovery`); `InternetTransport.cs`; `relay/src/index.ts` | Authenticate controller authority at the discovery/directory boundary. Do not equate the shared relay bearer with a controller credential. |
| Local setup permits downgrade | `installer/RemoteDebugger.iss` (`PrepareToInstall`, executable `ignoreversion` entry) | Compare installed and candidate release versions before shutdown/overwrite, retain legitimate same-version repair, and preserve role/settings. Exact .21 setup has demonstrably overwritten a newer installation. |
| Service setup can time out even elevated | `installer/RemoteDebugger.iss` provisioning stage; `src/RemoteDebugger/SupportInstaller.cs` | Diagnose the captured SCM 1053 startup first; collect stage, SCM, token and helper readiness evidence. Automatic consent and successful service startup are separate checks. |
| Failed connection retains progress | `src/RemoteDebugger/MainForm.cs` connection/synchronization exception and progress paths | Reset retained synchronization state consistently when entering terminal failure. The original .21 UI reproduction shows the refusal alongside Preparing update and Reconnecting. |

These are review targets, not implemented changes. The authorization change crosses transports; the release check should cover the matrix rather than a cosmetic disabled-button test alone.

## Separate harness and evidence work

Do not treat a test infrastructure limitation as a product failure. Patrice separately authorized the Hyper-V Harness project to implement, qualify and deploy its missing capabilities with Astra Max, then resume these tests. Normal installer launch requires supported Inno bootstrap-to-elevated-child identity tracking. Standard-user credential input and UAC decline need separate supported prompt modes. VM isolation, exact artifact identity, cleanup, and permission boundaries must remain intact. This authorization does not permit Remote Debugger product fixes.

The final results ledger must name every untested or partial scenario. Loopback, provisioned private VM networking, relay unit simulation, and a full packaged relay journey are distinct evidence scopes. A metadata-only update-begin test is not a completed update, reboot, or reconnect test.

## Review gate

Review confirmed failures, coverage gaps, compatibility effects, and this proposed order with Patrice. Only after agreement implement the smallest coherent authorization and installer changes, then rerun the failing scenarios plus their positive controls. Do not publish a replacement release from the diagnostic fixtures.
