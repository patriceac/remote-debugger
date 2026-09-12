# Release acceptance matrix

This matrix defines what a Release acceptance run must prove. A broker success
is evidence that the isolated guest was launched and cleaned up; it is not an
application pass. The Lab writes one `lab-result.json` per guest. Each check
has a `status` of `pass`, `fail`, or `blocked`, and records the evidence that
supports that status. A blocked check is never represented as a passing check.

The normal network run is two concurrent Release requests in the same
`IsolatedTestNet` cohort:

```powershell
./scripts/Build.ps1 -IncludeLab -Sign
./scripts/Test-HyperV.ps1 -Role Both -Scope Runtime -Cohort remote-debugger-acceptance
```

The bounded single-guest smoke run exercises the independent product flow when
the guest cannot provide secure-desktop consent for provisioning. It launches
the signed Release agent and controller inside one disconnected guest, binds
both to loopback, and still requires real code entry, exact-byte sync, fresh
frame/live telemetry, resources, input, regression, tray, and termination
evidence:

```powershell
./scripts/Test-HyperV.ps1 -Role Loopback -Scope Runtime -UpdateVariant None -Cohort remote-debugger-acceptance
```

Loopback results explicitly block LAN discovery, Private firewall, installed
broker, and UAC claims. Those gates remain owned by the two-VM `Both` run; the
loopback run does not turn local transport into LAN evidence.

For the complete workspace review, use `-Role LoopbackUi -Scope Runtime
-UpdateVariant None`. This extends the tray/session checks with all five tabs
before pairing, while connected, and after termination; invalid pairing input;
diagnostic templates, JSON validation, remote errors and cancellation; and recovery from an
invalid file path. It records tab screenshots and checks control availability,
visible bounds, connection identity, and scoped footers. It also changes the
disposable guest's Windows display setting to 150 percent and verifies the
Release window actually reports 144 DPI before recording scaled screenshots.
It checks that the assisted-PC explanation remains reachable by scrolling.
An unavailable scaling selector is reported as blocked, never a DPI pass.
Rendered screenshots require human/model visual review in addition to assertions.

The elapsed-time run is separate so its five-minute rotation and ten-minute
disconnect interval do not get confused with the shorter interaction checks:

```powershell
./scripts/Test-HyperV.ps1 -Role LoopbackLifetime -Scope Runtime -UpdateVariant None
```

It seeds a real unreachable saved connection, observes the displayed code
through its actual expiry, rejects that expired code, pairs with the new code
using Enter, and stops the guest controller process. It then observes the
agent's reconnect countdown and return to idle after expiry. Sleep-request queries
are explicitly blocked if the guest cannot run `powercfg /requests` with
the required Windows privileges. The normal Loopback run also leaves viewing
running for more than five minutes to check automatic stream renewal.

The signed update fixtures are built separately and are used only by the
provisioned update runs:

```powershell
./scripts/Build-UpdateFixtures.ps1
./scripts/Test-HyperV.ps1 -Role Both -Scope Provisioned -UpdateVariant Upgrade -Cohort remote-debugger-acceptance
./scripts/Test-HyperV.ps1 -Role Both -Scope Provisioned -UpdateVariant Downgrade -Cohort remote-debugger-acceptance
./scripts/Test-HyperV.ps1 -Role Both -Scope Provisioned -UpdateVariant SameVersion -Cohort remote-debugger-acceptance
./scripts/Test-HyperV.ps1 -Role Both -Scope Provisioned -UpdateVariant Rollback -Cohort remote-debugger-acceptance
```

For an update run the agent Lab starts the signed `older` fixture and the
controller Lab starts the signed `newer` fixture. `Downgrade` reverses that
pair; `SameVersion` uses the signed same-version/different-build fixture; and
`Rollback` arms the controller hash as a candidate, kills only the verified
candidate process before its startup health acknowledgement, then checks the
authenticated rollback snapshot. The fixture manifest records every hash.

`Both` starts one broker request for the agent and one for the controller. The
requests must be submitted through the SYSTEM broker; this script does not
launch the product on the physical host or manage a VM, switch, adapter, or
checkpoint. The Lab sends only a test coordination message over the approved
guest network. All pairing, RPC, deployment, input, streaming, and shutdown
claims come from the product under test.

`-Scope Runtime` covers the two-machine support session and records the
provisioning state. `-Scope Provisioned` additionally requires the one-time
administrator provisioning receipt and the installed private firewall rules.
The first-provisioning UAC desktop is a separate gate: the executable-testing
contract does not provide a secure-desktop consent driver, so a missing
provisioning receipt is recorded as `blocked` with that exact capability gap.
No Lab step clicks through or simulates UAC.

## Stable UI contract

The Lab uses native UI Automation and these IDs from `docs/UI_DESIGN.md`:

| Surface | Automation ID | Evidence expected |
| --- | --- | --- |
| Agent pairing code | `agentPairCode` | Exactly six ASCII digits, including leading zeroes |
| Agent countdown text | `pairingCountdownText` | Visible five-minute expiry/rotation countdown; `pairingCountdown` remains the visual progress bar |
| Agent state | `agentState` | Listening, preparation, pairing, reconnect, or termination text |
| Controller peer list | `peers` | Automatically discovered remote PC, with local PC excluded |
| Controller address | `host` | Selected discovered target |
| Controller pairing code | `pairCode` | Six-digit field that accepts `Enter` |
| Pair action | `pair` | Pairing/synchronization action state |
| Shared connection state | `connectionStatus` | Text plus connected/disconnected state; colour is not used alone |
| Session termination | `terminateSession` | Ends support and releases input/session state |
| Controller screen navigation | `navScreen`, `navProcesses`, `navFiles` | Selects the visible page before row, focus, or screenshot assertions |
| Remote screen | `remoteScreen` | Fresh displayed frame after pairing |
| Live badge | `liveBadge` | Visible `EN DIRECT` badge only after a fresh frame is presented |
| Stream state | `streamStatus` | Actual frame telemetry (fps/bitrate/capture latency) after a fresh frame |
| Process table | `processList` | Auto-loaded rows and sortable numeric columns |
| File table | `remoteFiles` | Auto-loaded remote workspace rows and sortable columns |
| Resource refresh | `refreshResources` | Explicit refresh remains available |
| File refresh | `browseFiles` | Explicit remote listing refresh remains available |

The normal controller flow must not require a fingerprint field, a fingerprint
checkbox, a separate agent start button, a separate pairing-open button, or a
revoke button. Technical certificate identity may remain available under a
diagnostic view, but it is not a pairing step.

## Agent checks

| ID | Requirement | Pass evidence | Scope |
| --- | --- | --- | --- |
| `agent.default_role` | Opening the Release starts the agent and shows **Donner le contrôle** first | Release window is ready, `agentState` is listening/preparing, and no start action was sent | Runtime |
| `agent.elevation_context` | The test records the actual guest token before provisioning decisions | `Native.IsElevated()` and the current user are recorded; this observation never turns a medium-integrity guest into a fake provisioning pass | All |
| `agent.pairing_code_format` | Pairing is the first useful action and the code is six digits | `agentPairCode` value matches `^[0-9]{6}$`; value is captured in the Lab result | Runtime |
| `agent.pairing_code_rotation` | Code remains stable until the five-minute expiry, then rotates and invalidates the old code | Capture the initial `MM:SS` countdown, observe the old code unchanged through the pre-expiry polling window, observe a new six-digit code within the expiry plus scheduler tolerance, and reject the old code through the real CLI PAKE exchange | Runtime-long |
| `agent.provisioning_command` | A permitted elevated guest can perform the one-time broker setup through the product contract | In an actually elevated Lab process, `--support-provision` exits successfully with the real signed Release hash and current interactive SID; in a medium-integrity guest the check is explicitly blocked | Provisioned |
| `agent.private_firewall` | Private/local-subnet firewall preparation is automatic | Product state plus read-only firewall query show only the product TCP/UDP rules on Private/LocalSubnet | Provisioned |
| `agent.sleep_request` | Sleep is held while the app is running | Product-owned `powercfg /requests` evidence and the visible state show the system request before pairing | Runtime |
| `agent.sleep_release` | Sleep is released after support ends | A read-only `powercfg /requests` query no longer contains a RemoteDebugger request | Runtime |
| `agent.maintenance_after_pairing` | Admin maintenance starts after pairing and lasts until support ends | Agent state, service/broker status, and paired maintenance command show one active session with no repeated UAC prompt | Provisioned |
| `agent.disconnect_grace` | A temporary disconnect gives ten minutes to reconnect, then revokes access and releases sleep | Controller disconnect is recorded, agent shows a real countdown, reconnect cancels it; a long run observes expiry and return to idle | Runtime-long |
| `agent.termination` | Termination cancels work and releases input/admin/sleep while keeping the application open | The agent displays Assistance terminée, rejects the old token, and offers Nouvelle assistance | Runtime |

The Lab records `Native.IsElevated()` before deciding whether setup is
possible. In an elevated guest it may invoke the product's supported
`--support-provision` verb with the real signed executable hash, interactive
user SID, and requesting process identity. It then reads the product's
protected provisioning receipt, stops the payload process, launches the
receipt's managed Program Files executable with the same data root, and
verifies platform status, service, and firewall through read-only queries. In
a medium-integrity guest the Lab does not simulate UAC. The current Hyper-V
executable contract cannot operate the Windows secure desktop, so the
provisioning command and first UAC portion of `agent.maintenance_after_pairing`
remain `blocked` until a provisioned guest or an elevated run supplies that
evidence. A Lab result must retain that block; it must not mark a visible
prompt or a guessed service as proof.

## Controller checks

| ID | Requirement | Pass evidence | Scope |
| --- | --- | --- | --- |
| `controller.discovery_on_launch` | Discovery starts automatically and excludes self | `peers` contains the remote machine without the controller machine | Runtime |
| `controller.code_enter_pairing` | Code plus Enter is sufficient | `pairCode` is filled and focused; `ENTER` is sent to that field; `pair` reports success | Runtime |
| `controller.no_fingerprint_gate` | No fingerprint checkbox is required | No `fingerprintVerified` control or fingerprint verification step; successful pair proves the normal path | Runtime |
| `controller.sync_before_live` | Agent matches the controller’s exact Release binary before live viewing | Controller and remote status hashes are equal; synchronization state is complete before `Live` | Runtime |
| `controller.sync_reconnect` | Restart/reconnect preserves pairing | Agent restart is induced through the test coordination channel; controller becomes connected again without a new code | Runtime |
| `controller.sync_rollback` | Failed update leaves a recoverable previous agent | Mismatched Release fixture, interrupted update, rollback receipt, and reconnect with the previous hash; the normal pre-live hash-match gate is intentionally not applicable while the replacement is being interrupted | Provisioned-fixture |
| `controller.live_auto_start` | Direct viewing starts automatically after pairing | `remoteScreen` receives a fresh frame, `liveBadge` says `EN DIRECT`, and `streamStatus` reports actual frame telemetry without clicking Start | Runtime |
| `controller.input_default` | Mouse and keyboard are enabled by default and release on focus loss | `remoteInputEnabled` is on; fixture receives a real mapped click/key; release evidence is recorded | Runtime |
| `controller.connection_pill` | Connection status is truthful and persistent | `connectionStatus` says Connected with the remote name only while heartbeat/frame evidence is current | Runtime |
| `controller.close_to_tray` | Close keeps the controller alive in the tray | Main window closes, controller process remains alive, heartbeat still succeeds, and tray Open restores it | Runtime |
| `controller.terminate` | Terminate support is clear and works from the controller | `terminateSession` invokes; agent returns to idle, old token is rejected, both applications stay open, and controller returns to discovery | Runtime |
| `loopback.input_preference` | Input preference survives pauses and page changes | Both the enabled and disabled checkbox states remain unchanged across pause/resume and navigation | Runtime |
| `loopback.agent_tray` | The receiving PC can hide and restore without ending support | Repeated window closes keep the process alive and tray Open restores a connected session after 21 seconds | Runtime |
| `loopback.tray_restores_live` | Restoring the controller resumes viewing | A fresh live frame appears automatically after a 21-second tray interval | Runtime |
| `loopback.latest_frame` | A slow presenter skips old frames | The Release CLI receives a 5 fps stream with a 1200 ms presenter delay; presented sequence numbers skip intermediate frames | Runtime |
| `loopback.second_session` | Support can restart without relaunching either application | Nouvelle assistance creates a code and the same two process IDs pair and display a fresh frame | Runtime |
| `loopback.agent_ends_session` | The receiving PC can end support | Both processes stay open; the agent returns to idle and the controller returns to connection | Runtime |
| `controller.resources_on_connect` | CPU, RAM, process, and file data load on connection | `processList` and `remoteFiles` contain rows; resource/file summaries have timestamps and non-placeholder values | Runtime |
| `controller.process_sort` | Process columns sort correctly | Header activation changes order; PID/CPU/RAM use numeric ordering and unavailable values remain last | Runtime |
| `controller.file_sort` | File columns sort correctly | Header activation changes order; name/size/date use typed ordering and folder grouping remains valid | Runtime |

`controller.sync_rollback` needs a deterministic older/newer signed Release
fixture. The build owner must place that fixture in the Lab payload and expose
its expected hash; the Lab never overwrites the running product or fabricates a
version label. Until then the check is `blocked`.

## Existing regression checks retained

The controller Lab continues to exercise the previous useful regression
surface after the new session flow: deploy fixture v1 and v2, verify running
PID/hash/file version, UI Automation inspection and text input, saved result
and log download, fresh screenshot, debugger attach/breakpoint/detach,
minidump, CPU/RAM/system/volume/network/services/events diagnostics, bounded
command and cancellation, continuous TLS stream with frame variation, stale
display geometry rejection, resumable upload, path traversal rejection,
idempotent mutation replay, application restart, hang/crash detection and
recovery, intervention history, and access denial after termination/revocation
where the product still exposes that operation.

Each regression row must assert an application result, RPC reply, downloaded
file, process state, or inspected UI value. Harness lifecycle fields such as
`HarnessSucceeded` are recorded separately and never stand in for those
assertions. Screenshots are evidence only after visual inspection confirms the
claimed state.

## Evidence and release gate

For each role, retain:

* `lab-result.json` with the Release SHA-256, role, checks, blocked capability
  details, and evidence paths;
* UI inventory and checkpoint screenshots for pairing, connected/live,
  processes, files, reconnect, and termination;
* downloaded fixture results/logs and stream metrics; and
* the broker result, GuestAgent result, cleanup result, worker identity,
  `VmFinalState=Off`, and deleted payload-child proof from the runner.

The final report must name the canonical `artifacts` payload and exact Release
hash, separate `HarnessSucceeded` from `TestEvaluated`/`TestPassed`, state the
network cohort and scope, list any blocked or skipped evidence, and report
cleanup warnings or retries. A Runtime pass is a two-machine product pass only
when both role results and both broker results meet those conditions. A
Provisioned/full pass additionally requires every provisioning and
rollback-fixture row to be `pass`.
