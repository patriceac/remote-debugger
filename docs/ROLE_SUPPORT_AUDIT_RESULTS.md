# Role and support audit results

Requirements and scenario IDs: [ROLE_SUPPORT_AUDIT.md](ROLE_SUPPORT_AUDIT.md).

Updated: 2026-09-25. Product fixes are not authorized. This ledger separates observed behavior from untested scope and test-infrastructure failures.

Follow-up in progress: Patrice separately authorized the Hyper-V Harness project to implement and deploy the missing UAC capabilities with Astra Max. That work is not deployed yet. Original packaged MCP, saved-grant reuse after process restart, the complete isolated LAN discovery matrix, and the actual installer controller-checkbox workflow have now been evaluated. Packaged local-relay coverage remains incomplete after two diagnostic setup failures.

## Status

The original 0.5.21 binary permits agent-originated support without controller authority. The same-source disposable-authority fixture reproduces access to a controller with its current receiving/maintenance switch OFF. All six role combinations have been exercised over loopback and provisioned VM networking; allowed positive controls work, and forbidden combinations also work. Full authorized updates and remote downgrade rejection work, but controller OFF accepts incoming updates and the exact standalone installer permits a downgrade. The original UI reproduces the contradictory failure/progress display. Automatic installer elevation remains unverified because the harness cannot follow this setup bootstrap's UAC handoff.

Of 37 tracked scenarios, **17 fail the agreed requirements, 13 pass within their stated scope, four are partial, and three are blocked**. These are scenario outcomes, not counts of independent bugs. Product code and the original release/installer bytes remain unchanged.

## Observed capabilities versus required policy

| Initiator -> receiver | Required | Ordinary support observed | Elevated command observed | Newer update admission observed |
|---|---|---|---|---|
| Agent -> agent | Deny | **Allowed** | **Allowed** | Denied: no controller proof |
| Agent -> controller OFF | Deny | **Allowed** | Denied by existing maintenance setting | Denied: no controller proof |
| Agent -> controller ON | Deny | **Allowed** | **Allowed** | Denied: no controller proof |
| Controller -> agent | Allow | Allowed | Allowed | Allowed; full replacement/reconnect also passed |
| Controller -> controller OFF | Deny | **Allowed** | Denied by existing maintenance setting | **Allowed**, including full replacement/reconnect |
| Controller -> controller ON | Allow | Allowed | Allowed | Allowed at begin; no full replacement claim |

Ordinary support includes the tested system/files/processes/screenshot/command/API operations. Actual text input also succeeded in all six rows, including all four required denials. The agent-to-agent case also ran on the original Release. Controller/elevated/update rows use explicitly identified disposable-authority fixtures. OFF/ON refers to the product's current maintenance setting: the required single Support enabled permission is absent. Bold Allowed entries violate the agreed role/receiving policy.

## Scenario ledger

| Case | Status | Evidence / limitation |
|---|---|---|
| E01 | PASS, exact elevated setup | Fresh original setup with /TASKS=!adminpc installs exact Release bytes and reports IsAdmin false. Final self-exiting diagnostic repeat passed application and harness checks. |
| E02 | PASS, exact setup checkbox | Actual checkbox starts unchecked; selecting it stages the protected pending envelope and launches an empty masked password prompt. With no password entered, before and after cancellation the installed original Release reports Agent and has no controller key. Explicit wrapper elevation only. |
| E03 | PASS, fixture password gate | Wrong disposable password visibly rejected; IsAdmin remains false. |
| E04 | PASS, exact setup cancellation and fixture | Closing the exact installed password prompt without input leaves Agent and no controller key. Closing after a rejected disposable password also leaves IsAdmin false. |
| E05 | FAIL overall; password enrollment PASS | Correct password creates controller authority, but incoming maintenance defaults On instead of required Off. |
| E06 | PARTIAL | Actual credential reimport preserves authority. Exact same-version agent reinstall preserves agent role and saved preference bytes with a clean final harness receipt. Enrolled-controller reinstall not exercised. |
| P01 | FAIL, original Release | Take control enabled; receiving maintenance On but editable. Required agent controls are disabled. |
| P02 | FAIL, fixture | Fresh enrolled controller receiving maintenance On/editable instead of Off. Required unified permission absent. |
| C01 | FAIL, original Release and provisioned fixture | Unenrolled agent pairs and performs screen capture, system/process/file listing, upload and commands. Fixture also allows elevated command. |
| C02 | FAIL, fixture | Agent pairs and performs ordinary support on controller Off. Current maintenance Off correctly rejects elevated command only. |
| C03 | FAIL, fixture | Agent pairs and performs ordinary and administrator support on controller On. |
| C04 | PASS, tested positive control | Controller to agent screen capture/files/processes/commands/admin operations work; supplementary actual text input also succeeds. |
| C05 | FAIL, fixture | Controller Off accepts ordinary support from another controller; existing elevated-command path alone denies. |
| C06 | PASS, tested positive control | Controller to controller On ordinary/admin operations work; supplementary actual text input also succeeds. |
| B01 | FAIL, including original packaged MCP | Agent Take control and --controller UI, manual-IP CLI pairing and connected API permit support. Original .21 agent also successfully calls MCP remote_status, remote_system and remote_upload; the authorized-controller positive control passes. |
| B02 | FAIL, complete isolated discovery matrix | All three agent-origin rows listed the target; controller-to-agent and controller-to-controller-ON positive controls passed. Controller-to-controller-OFF listing was observational. Windows Firewall consent remained open; this proves the isolated cohort's role policy, not deployment firewall readiness. |
| B03 | FAIL in relay backend unit scope | Shared network bearer lists computers/opens channels without controller proof in four existing relay tests. Packaged relay end-to-end not run. |
| B04 | FAIL, code and private-secret paths | Both a pairing code and a disposable private-network secret grant original Release agents support without a controller credential. Private-secret case is loopback, not a packaged relay journey. |
| B05 | FAIL, including process restart and MCP | admin-disable removes source authority; after restarting the same profile, the saved connection still completes CLI system and MCP status/system/upload operations. Other restart/resume variants are not exhaustively tested. |
| S01 | FAIL | Switching controller's current maintenance Off leaves session Connected and ordinary system call succeeds. |
| S02 | PARTIAL | Authority and explicit legacy Off survive process restart, real reboot and full forward update. Required unified support setting does not exist; enrolled-controller reinstall not exercised. |
| S03 | PASS, local UI and loopback | Actual agent-local End support rotates the invitation, rejects the old grant, preserves Agent role, and accepts a later session/system call. The separate controller-initiated session.end path also revoked access, but displayed a stale Support active footer. |
| S04 | FAIL | Fresh/missing and corrupt maintenance settings display On on controllers. Corrupt authority bytes remain unenrolled. |
| U01 | PASS, full fixture update | Authorized .22 fixture replaces installed .21 fixture; exact new bytes, relaunch, reconnect and subsequent system call verified. |
| U02 | PASS, fixture | Identical repeated sync returns alreadyMatched, no update, unchanged bytes/write time, support remains usable. |
| U03 | PASS, tested sender/receiver boundaries | Older real fixture sender is rejected and newer installed bytes remain unchanged; older receiver candidate metadata also denied. |
| U04 | PASS, tested sender/receiver boundaries | Actual CLI sync between different .21 binaries refuses with the strict-newer message and leaves target bytes/write time unchanged. Independent equal-version update.begin probes also deny. Diagnostic aggregate failed on a role-exit-code assumption and an extra post-refusal usability expectation; see receipt. |
| U05 | PASS at receiver begin boundary | All nine agent-origin combinations of target role/state and old/equal/new metadata reject with update_trust_rejected before transfer. |
| U06 | FAIL, exact installer | Original .21 setup exited zero and replaced installed synthetic .22 with the exact original .21 executable. Before/after hashes and versions verified. |
| U07 | PARTIAL | Exact same-version agent reinstall preserves role, saved preference bytes and Ready helper status; final application/harness checks passed. Enrolled-controller reinstall untested. |
| U08 | FAIL, full fixture replacement | Controller Off accepted an authorized newer update, installed new bytes, relaunched/reconnected, and allowed an ordinary system call. Its role and saved Off setting persisted. |
| I01 | BLOCKED, harness | Exact setup bootstrap starts before expected startup UAC contract observes consent. No automatic-elevation/helper verdict. |
| I02 | PASS, bounded elevated baseline | Exact setup succeeds through elevated GuestSetup; five Lab assertions passed. Separate clean explicit-UAC wrapper run confirms managed helper Ready/available/identityVerified and Agent role. Neither run proves setup's own automatic UAC. |
| I03 | BLOCKED, harness capability | No supported standard-user administrator-credential prompt entry. |
| I04 | BLOCKED, harness capability | No supported UAC decline action. |
| I05 | PARTIAL | Full fixture update replaces protected support successfully. Exact same-version reinstall reports Ready/available/identityVerified helper and preserves agent role/preferences with clean final harness checks. One separate elevated provision failed SCM 1053; controller reinstall remains untested. |
| F01 | FAIL, original Release UI | Actual version refusal appears alongside Preparing update, In progress and Reconnecting. Reproduced through real Connect UI; no driver/input failure. |

Statuses: PASS / FAIL are behavioral results against the agreed contract. BLOCKED identifies a harness/fixture/capability limitation. SOURCE ONLY is not runtime verification. PARTIAL must name the untested portion.

## Coverage still open

- **Normal setup elevation, standard-user credential entry and declining UAC (I01/I03/I04):** blocked by the broker's bootstrap/prompt capabilities. Explicit wrapper consent and elevated GuestSetup are useful comparisons, not substitutes for these journeys. No cause is assigned to the reported family-PC helper error.
- **Enrolled-controller reinstall (E06/S02/U07/I05):** exact original agent install/reinstall and fixture controller restart/reboot/forward upgrade passed their persistence checks. A full reinstall preserving a credential trusted by the shipped executable was not run; real controller credentials were not used.
- **Transport and entry-point scope:** relay directory/channel behavior was exercised in the local worker simulator; the complete packaged local-relay journey is still unverified. The first attempt stopped at a disposable certificate-trust prompt; the second stopped because the normal driver could not find the elevated setup's receipt. Neither reached a relay policy case. The full isolated LAN discovery matrix, original packaged MCP and saved-grant reuse after authority removal/process restart are now verified. The LAN capture retains a Windows Firewall prompt and `firewallReady:false`, so it does not qualify production firewall setup. No deployed Cloudflare journey or every restart/resume variant has been claimed.
- **Version scope:** original .21 and clearly marked same-source .21/.22 fixtures were used. The historical .20 installation and every possible version/transport permutation were not exercised.

None of these gaps is a pass. They must remain visible when agreeing the corrective plan and its release checks.

## Proposed corrective actions

See [ROLE_SUPPORT_CORRECTIVE_PLAN.md](ROLE_SUPPORT_CORRECTIVE_PLAN.md). It is a proposal for review; no product correction has been made.

## Follow-up VM receipt: actual controller checkbox and password cancellation

- Request `executable-test-20260925T021016947Z-d879ec42`; exact original setup SHA-256 `7F7ABADADA531BC1E547850F855BD53DF1C90C7D5F991F6BFE57A5382F17F353`, read-only VHDX input, Network None. Diagnostic SHA-256 `E139C549BFC6FC5392153A1CEDC52B731438AF5902778356CA0603451830DA53`.
- The real additional-tasks page showed the controller/admin-PC checkbox initially unchecked. Native accessibility identified the actual checkbox and current rectangle; selection was verified through its checked state and before/after screenshots.
- Setup installed the original Release hash `95199EA784F4E5D983F770E06D5802F22AD8470104CDDBA0C440E2344D919511`. Before any controller password, actual `admin-status` returned `ok:true,isAdmin:false`; the protected pending envelope existed and `update-admin.dpapi` did not.
- Finish opened the controller-password dialog. UIA verified `IsPassword:true`; the inspected screenshot shows an empty field. Closing it without input left the role Agent and controller key absent. This closes E02 and corroborates E04 for the exact package.
- Harness and application assertions passed, no cleanup failure, verified guest process cleanup without survivors, VM Off, payload and read-only input children deleted. Wrapper startup UAC was accepted and hash-verified. This is explicit diagnostic-wrapper elevation, not proof of I01 setup self-elevation.
- Earlier attempts `executable-test-20260925T015103498Z-6558a999` and `executable-test-20260925T015811680Z-b1c5b874` stopped at diagnostic UIA/MSAA selector limitations before selection or installation. They cleaned up but supply no E02 product verdict. The completed repeat above used the observed accessibility rectangle; product bytes were unchanged.

## Follow-up VM attempts: packaged local relay remains unverified

- `executable-test-20260925T014251771Z-e6bdded1` verified the provisioned fixture, then stopped at Windows certificate-trust consent while importing the disposable local relay certificate into CurrentUser/Root. No prompt input was sent. Broker cancellation completed with VM Off, payload child deleted and Network None disconnected. The cancelled run has no guest process-cleanup assertion or relay policy verdict.
- `executable-test-20260925T021100610Z-5a9a2052` moved synthetic certificate creation/trust into the manifest-bound elevated GuestSetup. Setup exited zero and its broker-captured receipt confirmed the exact fixture hash and LocalMachine/Root trust. The normal driver then verified the managed product but failed to read `relay-state/fixture-setup.json` before starting the relay. Both phases were passed the same request Outbox path; the receipt was absent from the driver phase and collected evidence. The exact cause of its disappearance was not established by that receipt.
- The second harness execution succeeded and evaluated the failed driver assertion. Its result has a diagnostic fatal error, not a role-policy result. Verified process cleanup had no survivors, VM Off, payload child deleted, adapters disconnected, no cleanup failure; worker recycling was asynchronous. No relay matrix case ran.
- Both attempts use the same-source disposable-authority .21 fixture and a proposed local Miniflare/workerd relay with no Internet route. Neither is evidence of deployed Cloudflare behavior. Test-source preparation now keeps synthetic cross-phase state outside the request Outbox; it has not been rerun, and submissions are held for the harness release drain.

## Follow-up VM receipt: packaged MCP and saved grant after restart

- Request: `executable-test-20260925T013239904Z-9a13c5a6`; evidence under the same-named directory in `D:\Disk\VMs\Codex-Harness\Live\Broker\Results`.
- Original .21 agent-origin case is individually bound to Release SHA-256 `95199EA784F4E5D983F770E06D5802F22AD8470104CDDBA0C440E2344D919511`. Controller positive and authority-removal cases use the same-source disposable-authority .21 fixture SHA-256 `E444270C10BE5D9DF2E6BABB6684CEE707C7797DFD531E431BCA2676D3BA2C24`.
- Actual packaged Node MCP server and SDK session, separate source/target profiles, Network None. The unenrolled original agent successfully called `remote_status`, `remote_system` and `remote_upload`; the 29-byte marker was verified in the target workspace. All three required denials failed. The enrolled-controller positive control accepted the same operations.
- After successful `admin-disable` (`changed:true`), CLI role status was false. The same source profile was restarted, remained an agent, and reused its saved connection for a successful CLI system call and the same three MCP operations. This extends B05 to process restart; it is not a reboot or every resumed-session variant.
- Application result: evaluated, `fatal:null`, `passed:false` from the expected policy checks. Every nested MCP session had `fatal:null`. No transport/SDK failure was classified as a denial.
- Harness and guest execution succeeded, no infrastructure retry, process cleanup verified with no survivors, VM Off, all adapters disconnected, no host inputs, payload child deleted and path independently absent. Evidence snapshot copied 26/26 files without skips or warnings.
- Visually inspected captures: `agent-origin.png`, `controller-positive.png`, `B05.restarted-agent.png`, and `mcp-role-final.png`.

## Follow-up VM receipt: all six LAN discovery combinations

- Receiver: `executable-test-20260925T013228701Z-38d244fd`; sender: `executable-test-20260925T013312060Z-7f27c598`. Canonical package `work/role-permission-audit/network-suite`; disposable-authority .21 product SHA-256 `E444270C10BE5D9DF2E6BABB6684CEE707C7797DFD531E431BCA2676D3BA2C24`.
- Both provisioned guests verified managed product bytes and helper availability. Actual CLI role status matched all source/target fixtures; every receiver UI reported Ready before the paired discovery request. `IsolatedTestNet` cohort `role-audit-b02-20260925-a`; no physical LAN or Internet route.
- Actual CLI discovery found target `10.254.0.101` for agent-to-agent, agent-to-controller-OFF and agent-to-controller-ON: all three required denials failed. Controller-to-agent and controller-to-controller-ON found the target and passed their positive controls. Controller-to-controller-OFF listing was recorded without inventing a visibility prohibition. All results had `usedLanFallback:false`.
- This run exercised discovery only. Both terminal screenshots retain a Windows Firewall consent prompt for the diagnostic driver; it was not accepted. Product status recorded `firewallReady:false`. The broker's isolated-cohort networking allowed the exchange. These facts do not invalidate the observed role-policy failure, but they do not prove production firewall readiness or physical-LAN operation.
- Both harnesses succeeded with no retry; receiver assertion passed, sender assertion failed for the three forbidden listings. Both guest process-cleanup records succeeded and were verified, with no survivors or errors. Both VMs Off, payload children deleted and their paths independently absent, all request adapters disconnected. The terminal schema has no separate disk-attachment inventory. The concurrent sender cleanup recorded the shared switch retained; the final receiver cleanup confirmed that switch and cohort state were removed.
- Detailed local notes: `work/role-permission-audit/network-followup/b02-discovery-evidence.md`. No product or driver correction was made during this run.

## VM receipt: original Release role boundary

- Request: `executable-test-20260924T230729618Z-42af46b0`.
- Evidence directory: `D:\Disk\VMs\Codex-Harness\Live\Broker\Results\executable-test-20260924T230729618Z-42af46b0`.
- Exact Release SHA-256: `95199EA784F4E5D983F770E06D5802F22AD8470104CDDBA0C440E2344D919511`; file version 0.5.21.0.
- Both participating product processes used separate fresh data roots in one guest. The CLI reported `isAdmin:false` for both. Guest process was not elevated.
- Harness succeeded; assertion was evaluated and failed against the required policy; application test fatal error was null.
- Transport: loopback only. No LAN-discovery or relay claim.
- Captures: `agent-to-agent-agent-role-switch.png`, `agent-to-agent-before.png`, `agent-to-agent-after.png`, `final.png`, plus the remote screenshot.
- Cleanup: VM Off; process cleanup succeeded; payload and read-only input child VHDXs deleted; no warnings or missing requested evidence.

## VM receipt: remaining role matrix with disposable controller authority

- Request: `executable-test-20260924T231113870Z-6a1a904e`; same-named directory under `D:\Disk\VMs\Codex-Harness\Live\Broker\Results`.
- Fixture SHA-256 `B46223FC6D04BCD105752769017CD193DAF8872A6DCFFD07715E697DDBC917CD`, version 0.5.21.0; same source with repository public test update authority, not the shipped executable.
- Every source/target root was checked with the actual CLI `admin-status` before testing. Each target's explicit switch state was checked through UIA. Non-elevated guest, loopback only.
- 60 driver checks: 29 passed, 26 failed, five administrator-operation checks explicitly blocked because no helper was provisioned. Counts include setup/precondition checks, not 60 independent user scenarios.
- Harness succeeded, application assertion failed, fatal null. VM Off, process cleanup and payload/read-only-input child deletion succeeded, no warnings or missing evidence.
- Screenshots include `controller-default.png`, `agent-to-controller-off-before.png`, `controller-to-controller-off-before.png`, and `controller-to-controller-on-after.png`. The first OFF screenshot visibly shows Admin mode and the switch Off while a pairing code is offered.

## VM receipt: exact installer elevated baseline

- Request: `executable-test-20260924T231227219Z-450039b5`; exact installer SHA in requirements document.
- GuestSetup bound the original setup executable and hash, ran as `CODEX-W11\CodexTest`, IsAdministrator true, and exited zero. Network None.
- Existing `Test-InternetInstaller.ps1 -LockedSetup` Lab passed all five required assertions: clean initial profile, setup completion, original-user shortcuts/build identity, valid locked profile envelope, and first-launch passphrase required.
- Screenshots `installed-passphrase-required.png` and `installer-final.png` were inspected. The locked private-network profile was never unlocked; no production relay was contacted.
- This Lab does not independently export SCM service state. Zero setup exit traverses the product path that waits for helper readiness; independent helper-state tests are separate.
- Process-tree cleanup verified no survivors. VM Off and payload child deleted; no warnings or retries.
- I01 remains blocked: the outer setup manifest is `asInvoker`, while Inno source declares `PrivilegesRequired=admin`. The harness exact-image startup-UAC contract rejects an already observed bootstrap process before consent handoff. This alone neither proves missing elevation nor explains the reported helper failure. I03/I04 require unsupported prompt modes.

## Focused unit evidence

- Existing Core protected-envelope/update-proof/update-policy tests: 26/26 passed.
- Existing Platform controller-credential/static-installer/import/mocked-discovery tests: 27/27 passed.
- Existing matching-snapshot/no-transfer synchronization test: 1/1 passed.
- These prove the tested primitives and current expectations. They do not establish role enforcement, packaged UAC, relay end-to-end behavior, or real update replacement/reconnect. Exact filters and TRX pointers are retained in `work/role-permission-audit/unit-evidence`.

## Enrollment driver limitations retained for auditability

Requests `executable-test-20260924T231211489Z-3346a1b3`, `executable-test-20260924T232243268Z-7b277145`, and `executable-test-20260924T232747482Z-2583d827` used default smoke actions and were terminated before completing the enrollment sequence. Earlier suspected UIA stalls are **not established**; actual action evidence showed no result-file wait. Wrong-password rejection was recorded before termination in the second of these. Request `executable-test-20260924T231749175Z-9af8d958` did produce a result with an overly strict foreground-return check failure in the driver. All completed broker cleanup. None is a product enrollment failure. The final revised request explicitly waits for the result file.

## Provisioned two-VM network findings

- Receiver request `executable-test-20260924T232359016Z-cb6ac458`; sender `executable-test-20260924T232359016Z-64d81c67`.
- Signed same-source 0.5.21 fixture SHA `E444270C10BE5D9DF2E6BABB6684CEE707C7797DFD531E431BCA2676D3BA2C24`. Both guests provisioned it, verified identical Program Files bytes, then confirmed helper identity/availability from that managed executable. The initial portable CLI identity was rejected; the driver did not mistake that for a usable helper.
- All six role combinations were exercised across the broker's VM-only network. Agent-originated pairing, `system`, ordinary `command`, and connected-API `system` succeeded against agents and controllers, including controller OFF.
- Agent-to-agent and agent-to-controller ON also executed an elevated command whose output confirmed `IsSystem=True`. Both source roles were denied `maintenance.elevated` by a controller with the existing maintenance preference OFF. This confirms that the current switch gates that administrator path but leaves ordinary support open.
- Authorized-controller positive controls passed the same ordinary and elevated operations to agent/controller ON.
- An unenrolled agent obtained a discovery listing containing the controller-OFF peer. Other discovery listings were empty, including expected-positive controller cases. The driver started discovery before confirming the target's asynchronous network preparation; those empty listings do not establish role enforcement.
- Input returned `operation_failed: Windows refused foreground focus; no input was sent` in all rows. This is **inconclusive input coverage**, including rows whose original Boolean assertion accidentally counted it as a denial pass.
- Receiver screenshots showed an unaccepted firewall prompt for the test coordinator executable. The later repeat handled the exact-application prompt, but was blocked by sender helper provisioning, as recorded below.
- Update subchecks were blocked by a test-driver operation-name error (`update.status` without a transaction instead of `update.snapshot`). No update-begin/transfer/replacement was exercised in these requests; the separate corrected update probes below provide that evidence.
- Both harness runs completed, process cleanup and payload-child deletion succeeded, both VMs Off. The broker attested the same IsolatedTestNet cohort with no host/LAN/Internet route, no default route/DNS and IPv6 disabled; network teardown succeeded.

## Relay worker unit simulation

Four existing TypeScript relay tests passed using disposable bearer keys in the local Cloudflare worker simulator: fingerprint/listing, live-client discovery, bidirectional channel transport, and legacy/protected-network separation. They obtain a directory listing and open a controller channel with network membership credentials, without a separate controller-role proof. This confirms the current backend unit behavior; it is not a full packaged app/relay journey. The test command exited zero but emitted a workerd teardown warning; preserve that warning instead of calling the run cleanup-error-free.

## Receiver update protocol boundary

- Request `executable-test-20260924T234246649Z-fbce7c82`; signed same-source 0.5.21 fixture, SHA `E444270C10BE5D9DF2E6BABB6684CEE707C7797DFD531E431BCA2676D3BA2C24`. Network None, separate loopback roots.
- Thirty checks comprise twelve role/pairing preconditions and eighteen `update.begin` probes. Pairing preconditions deliberately use the current permissive behavior; they are not authorization passes.
- All nine agent-origin probes returned `update_trust_rejected`, for candidate versions 0.5.20, 0.5.21, and 0.5.22 against agent/controller OFF/controller ON.
- All six authorized-controller probes declaring an older or equal version returned the strict-newer rejection. Authorized newer metadata was accepted for agent and controller ON, as expected.
- Authorized newer metadata was also accepted for controller OFF: `ok:true`, transaction ID, offset zero. This is a policy failure even before transfer. Accepted transactions were cancelled; this test did not transfer, replace, or reconnect.
- Harness succeeded, assertion evaluated and failed, fatal null. VM Off; process cleanup, payload/read-only-input deletion succeeded; no evidence warnings.

## Restart, session end, and corrupt-state boundary

- Request `executable-test-20260924T233948491Z-c05f65e8`; synthetic same-source 0.5.22 fixture SHA `9E029DD01ADCFB349EE6DB0E4354FE553C6C36EF65AD1DBCE556542A57994D5B`, Network None.
- Eighteen checks; seventeen passed. Enrolled controller authority and explicitly saved legacy maintenance Off survived both process restart and a real guest reboot. The outgoing controller UI remained available.
- Broker restart contract was proven: boot changed from 23:41:29Z to 23:49:51Z, 15.13 seconds observed signed out, one manual guest sign-in, no replay. The continuation intentionally launched the product to inspect persistence; this is not proof of product auto-start.
- Malformed maintenance JSON displayed On, failing the desired fail-closed behavior. Corrupt credential bytes produced `isAdmin:false`.
- Ending an agent session through the controller's `session.end` RPC succeeded, rotated the code, rejected the old grant, left the process running, and preserved `isAdmin:false`. This did not click the agent-local End support button. The screenshot's Support active footer is a UI-state observation; it does not override the demonstrated grant rejection.
- Harness succeeded, assertion evaluated and failed, fatal null. VM Off, cleanup verified, payload child deleted, no warnings. Screenshots of Off before/after restart and reboot, corrupt On, ended support, and signed-out state were inspected.

## Completed password enrollment sequence

- Request `executable-test-20260924T234415148Z-df00f47e`; unsigned same-source 0.5.21 test-authority fixture SHA `B46223FC6D04BCD105752769017CD193DAF8872A6DCFFD07715E697DDBC917CD`, Network None.
- Seventeen checks, sixteen passed. The actual `cli admin-import` stages the encrypted credential without creating authority. Wrong password is rejected with a visible message; closing that prompt leaves `isAdmin:false`. Correct password yields `isAdmin:true`. Reimporting the credential preserves authority.
- The newly enrolled controller displayed maintenance On/editable, failing the required Off default. It is still the legacy maintenance setting, not the required unified permission.
- This exercises the product credential-import and password UI paths using the repository's disposable test key and password. It does not click the checkbox in the shipped installer or use a real controller password.
- Harness succeeded; assertion evaluated and failed, fatal null. VM Off, process/input/payload cleanup succeeded, no warnings. `enrollment-correct-controller.png` was inspected.

## Network repeat blocked by helper provisioning

- Corrected receiver `executable-test-20260924T234314053Z-85d082b9`; sender `executable-test-20260924T234314053Z-5e3ccb84`. Same signed 0.5.21 fixture as the first network run.
- Sender's exact-hash `cli platform-provision` ran with an administrator token, but exited 2 after about 212 seconds. SCM returned **1053: service did not respond in time** while starting the support service. Product reported no privileged capability ready. No normal sender driver or role row ran.
- This is a real provisioning failure in the disposable fixture environment. It resembles the reported helper problem but does not establish its historical cause, prove an elevation-order defect, or reproduce the original installer path. Other guests provisioned the same bytes successfully.
- The receiver was cancelled through the broker after its peer became unavailable. Both requests have TestEvaluated false; both VMs Off, network cleanup true, payload children deleted. No new network-policy verdict is available from the repeat. Detailed process-cleanup and evidence-warning fields were not reported for these failure/cancellation results.

## Completed forward update and downgrade refusal

- Request `executable-test-20260924T234804873Z-177cb5f3`; signed disposable-authority .21 (`E444270C...`) installed through elevated GuestSetup, then signed same-source synthetic .22 (`9E029DD0...`) as authorized sender. Network None/loopback; normal test driver unelevated.
- All nine application checks passed. The actual CLI sync transferred and replaced the managed executable with the exact .22 hash, relaunched protected support, confirmed the update, and reconnected. A subsequent ordinary system request succeeded.
- Repeating sync returned `alreadyMatched:true`, `updated:false`; installed hash and last-write timestamp were unchanged. Invoking sync from the older real .21 fixture then returned the strict-newer refusal and left .22 bytes unchanged. The receiving agent remained unenrolled.
- Initial platform status from the portable .21 path was IdentityRejected as expected. The test launched the verified managed bytes; successful privileged replacement/health confirmation is separate from that initial portable status.
- Harness and declared application assertion both succeeded, fatal null; VM Off, payload child deleted, process cleanup verified without survivors, no evidence warnings. Before/after screenshots inspected. Evidence collection retried after one interrupted transfer and ultimately completed. This qualifies the tested disposable fixture update path, not an upgrade on a real family PC or a shipped .22 release.

## Controller Off accepts full update and subsequent support

- Request `executable-test-20260924T234838050Z-1b5acfc5`, same signed .21/.22 fixtures and disconnected loopback setup as the positive control above.
- Actual target `admin-status` was true. The before-update screenshot visibly shows Admin mode, version 0.5.21, and maintenance Off. The source was separately verified as an enrolled controller.
- Sync returned success and replaced installed .21 hash `E444270C...` with exact .22 hash `9E029DD0...`, despite the expected denial. Relaunch/reconnect completed; an ordinary system request then succeeded despite expected denial.
- Ten checks: eight passed, two failed (forbidden update and forbidden post-update support). Identical sync avoided rewriting bytes, old-sender downgrade remained refused, controller authority and the saved Off preference were preserved.
- This proves lack of receiving-setting enforcement, not accidental loss of the setting or role. A real shipped-version controller with real credentials was not used.
- Harness succeeded, application assertion failed, fatal null. VM Off; process cleanup verified without survivors, payload child deleted, adapters disconnected, no evidence warnings. Before-update screenshot inspected.

## Original UI reproduces contradictory terminal failure

- Request `executable-test-20260924T235459610Z-f65a1e76`; exact original .21 SHA `95199EA7...`, synthetic same-source .22 receiver SHA `9E029DD0...`. Network None/loopback; actual manual-address/code fields and Connect UI exercised.
- The version-refusal assertion passed. The terminal-state assertion failed: after the actual refusal and an additional 2.5 seconds, the progress panel still said Preparing update and In progress; the footer visibly said Reconnecting. No driver fatal/input timeout occurred.
- `failed-downgrade-ui.png` was visually inspected and reproduces the supplied screenshot's contradiction. Header lookup was empty in UIA; the reconnect claim comes from the visible footer, not that missing field.
- Harness succeeded, declared assertion failed; VM Off, process cleanup succeeded without survivors, payload child deleted, no evidence warnings. This confirms F01 without attributing the historical family-PC session state.

## Exact standalone installer permits downgrade

- Corrected request `executable-test-20260924T235116551Z-8443bb0c`; exact original setup SHA `7F7ABADADA531BC1E547850F855BD53DF1C90C7D5F991F6BFE57A5382F17F353`. Disconnected elevated sequence, with evidence persisted separately from the normal driver's output directory.
- Before: managed Program Files executable version 0.5.22.0, SHA `9E029DD01ADCFB349EE6DB0E4354FE553C6C36EF65AD1DBCE556542A57994D5B`. Original .21 setup exited zero. After: version 0.5.21.0, SHA `95199EA784F4E5D983F770E06D5802F22AD8470104CDDBA0C440E2344D919511`.
- Driver fatal null, `downgradePrevented:false`. This directly fails U06. The preinstalled newer version is a same-source synthetic fixture; the older installer and resulting installed .21 bytes are the actual shipped artifacts.
- Final original product reported `isAdmin:false`; elevation did not make it a controller. Independent SCM query reported RemoteDebuggerSupport Running as LocalSystem. This is service-state evidence, not a complete interactive helper/readiness assertion.
- `older-setup.log` records installation of `RemoteDebugger-Admin.rdadmin` and Administrative install mode Yes. The source places that file and the controller task in the same `AdminCredentialPath` preprocessor branch, supporting that the feature is compiled into this package. It does not prove an actual checkbox click/selected state. No credential contents were inspected.
- Harness succeeded, application assertion failed; VM Off, process cleanup succeeded without survivors, payload child deleted, Network None/disconnected, no evidence warnings. The final screenshot shows a blank guest desktop, so it does not qualify installer UI behavior; the actual before/after file identities, setup log and result assertion establish the downgrade. The earlier lost-result request remains an infrastructure limitation and is not used as downgrade evidence.

## Earlier explicit-elevation reinstall blocked before start

- Request `executable-test-20260925T000848561Z-a0bb779b` was a separate diagnostic wrapper with an explicit requireAdministrator manifest, exact original setup as a read-only input, and two intended same-version agent installations. It would not have qualified automatic elevation of the installer itself.
- During repeated worker readiness failures, the broker reported no ready workers, then degraded pool invariants/orphaned processing. Immediately before cancellation this request was still Queued with no worker or application PID. The supported cancellation returned CancelledBeforeStart and removed that exact queued request.
- No UAC, setup, reinstall, or application assertion ran in this request. The later run below supplies application evidence for E01, agent U07 and corresponding I05; no host fallback or harness repair was attempted.

The pool subsequently recovered to three ready workers. The first supplementary-input driver displayed a missing .NET runtime dialog before running product code: the two newest diagnostic packages were incorrectly framework-dependent. They were republished into new task-local self-contained output directories. This is diagnostic packaging work only, not a product or harness fix. Earlier cancellation/startup failures remain recorded separately.

## Agent-local End support and later support

- Request `executable-test-20260925T002605559Z-e5fd5dde`; same-source disposable-authority .21 fixture SHA `B46223FC6D04BCD105752769017CD193DAF8872A6DCFFD07715E697DDBC917CD`. Network None/loopback.
- All eight checks passed. The actual agent-local End support UI control was invoked, the invitation rotated, and the prior controller grant returned `access_denied: This support session is not authorized or has ended`. The agent stayed `isAdmin:false`.
- A new pairing with the new invitation succeeded and a subsequent system call returned success. This is direct evidence that ending one support session does not convert the agent or prevent later support.
- `S03.local-ended.png` shows the new invitation and waiting state; `S03.new-session.png` shows the new connected session. Both screenshots were inspected.
- Harness and application assertion passed. VM Off, process cleanup verified with no survivors, payload and read-only fixture children deleted, no warnings. No LAN/relay claim.

## Exact agent installation and reinstall: application pass, cleanup failure

- Request `executable-test-20260925T002144255Z-434ffe27`; exact original setup SHA `7F7ABADADA531BC1E547850F855BD53DF1C90C7D5F991F6BFE57A5382F17F353`, immutable read-only VHDX input, Network None.
- The broker accepted and verified startup UAC for the separate `requireAdministrator` audit wrapper. This proves the wrapper's consent, not automatic elevation by the original setup. No GuestSetup was used.
- All twelve application checks passed: fresh guest, two setup exits zero with `/TASKS=!adminpc`, exact installed Release hash `95199EA7...` after each, actual `isAdmin:false` after each, and saved legacy preference bytes preserved through reinstall.
- Both managed `platform-status` calls reported Ready, available, identityVerified and interactiveInputAvailable, with service version 0.5.21.0. FirewallReady was false in the disconnected guest. No enrolled-controller reinstall or full transfer workflow was exercised.
- The final screenshot showing all twelve checks and both UAC captures were inspected. The harness nevertheless failed `ProcessCleanup`: elevated diagnostic window PID 8500 survived the medium-integrity cleanup command. VM Off, payload/input child deletion and network disconnection succeeded; no evidence warnings.
- The final diagnostic-only repeat below saves its own screenshot and closes the audit window after writing the result. The earlier run remains an observed application pass with failed harness cleanup; it is not retrospectively relabeled clean.

## Exact agent installation and reinstall: clean qualification

- Request `executable-test-20260925T003407502Z-6aaf5554`; same exact setup and two-install scenario as above. Only the diagnostic wrapper's completion behavior changed; no product or harness change.
- All twelve application assertions passed again. Both original setup runs exited zero, installed the exact .21 Release hash, retained `isAdmin:false`, reported managed helper Ready/available/identityVerified, and preserved saved legacy preference bytes. Service version was 0.5.21.0.
- Explicit startup UAC was accepted and verified for the hash-bound elevated wrapper. This remains an explicit-elevation comparison, not a normal setup-launch UAC verdict.
- `audit-driver-final.png` and both UAC captures were inspected. Harness and application assertion passed, process cleanup verified without survivors, VM Off, payload and read-only setup child deletion succeeded, Network None/adapters disconnected, no warnings or retries.
- This closes the prior diagnostic-window cleanup gap for fresh agent install and agent reinstall. The controller checkbox's interactive selection, enrolled-controller reinstall, standard-user credentials and declined/automatic setup UAC remain separate coverage gaps.

## Actual input matrix and private-network-secret bypass

- Request `executable-test-20260925T002827442Z-de68af9d`; canonical artifacts payload, Network None/loopback. Agent-to-agent and disposable private-secret cases use the exact original .21 SHA `95199EA7...`; controller rows use signed same-source test-authority .21 SHA `E444270C...`.
- Every role precondition passed. Actual `ui.text` requests typed a unique marker into the guest diagnostic text box in all six role combinations. Both allowed controller rows passed; all four required-denial rows failed authorization expectations. Controller-OFF screenshots visibly retain the Off setting during accepted input.
- The agent-to-agent driver's preliminary focus call returned false, but the actual remote request returned `ok:true`, `typed:true`, and the marker appeared. Unlike the earlier provisioned network run's foreground errors, this is observed input acceptance.
- A disposable private profile was imported into fresh original-Release agent roots. Both remained unenrolled. The shared private-network secret allowed pairing and a successful system call. The configured dummy relay was unavailable; the screenshot shows that alongside the local connected session. This proves a loopback secret bypass and makes no real-relay claim.
- All six input screenshots, the private-secret screenshot and completion capture were inspected. Harness succeeded, application assertion failed as expected for policy violations, fatal null. VM Off, process cleanup verified without survivors, payload child deleted and absent, all adapters disconnected, no evidence warnings.
- Earlier supplementary attempts supplied no product-policy result: `executable-test-20260925T000406017Z-6d305d6b` lacked the diagnostic runtime; `executable-test-20260925T002226537Z-faf565fb` then failed to launch the original binary from the auxiliary read-only VHDX with Win32 error 999 before any checks. Both cleaned up. The final canonical-payload request above is the qualifying result; no host execution or harness repair was used.

## Actual equal-version, different-binary synchronization refusal

- Request `executable-test-20260925T003926548Z-ec1fd9cd`; Network None/loopback. Target: original .21 SHA `95199EA7...`; sender: signed same-source disposable-authority .21 SHA `E444270C...`. Actual versions matched and hashes differed; source enrollment and pairing succeeded.
- Actual CLI `sync` returned exit 2 with the strict-newer message, expressly rejecting replacement builds with the same version. Target SHA-256 and last-write timestamp remained unchanged. This passes U04's replacement-refusal requirement.
- This is a **client preflight** refusal: `AgentUpdateService.cs` calls `UpdatePolicy.RequireNewerRelease` before challenge/begin. The separate protocol matrix above establishes the independent receiver equal-version boundary. No transfer should occur after this refusal.
- The aggregate diagnostic assertion failed on two additional checks. Its target-role check incorrectly required exit zero: `Program.cs` intentionally returns exit 1 alongside `ok:true,isAdmin:false` for an agent. The observed role was correct. The extra post-refusal system call returned the same version mismatch because the binaries still differed; continued cross-build support was not an agreed U04 requirement and is not counted as a new policy failure.
- The inspected screenshot retained Synchronizing/Preparing transfer after refusal, corroborating F01's stale-progress observation. The failed aggregate assertion is retained; the run is not relabeled an overall application-test pass.
- Harness succeeded, fatal null, no retry. Process cleanup and residue cleanup succeeded, VM Off, payload child deleted, evidence collected. No physical-host application execution.
