# Validation record

## 0.2.1 — input, window lifetime, and session recovery

September 12, 2026: **122/122 unit tests passed** (91 Core and 31 platform), with a Release build producing zero warnings and zero errors. New regression tests cover pointer coalescing, click/key ordering, pending-input release, preference preservation after transport failure, repeated native window closure, explicit Quit, Windows shutdown, and the distinction between ending support and exiting for an update.

The signed self-contained Release executable has SHA-256 `E30E59653952C2517B37355C3D3826E1319CE9B0ACB1DFF47E42744FD32BE58D`. Its publisher is the locally enrolled publisher documented below; it is not a public CA certificate.

Request `executable-test-20260912T090520555Z-e20c053d` exercised this exact executable with two GUI processes in a disconnected Hyper-V guest. The Lab was submitted from its canonical `artifacts/lab` directory, with canonical `artifacts/release` supplied as a read-only VHDX input. **All required checks passed: 28 checks passed in total**, with three optional checks explicitly blocked and no fatal error. The run verified:

- Six-digit Enter pairing, exact running-binary identity, fresh automatic viewing, and enabled input by default.
- Preserving both checked and unchecked input preferences through pause/resume and workspace navigation.
- Closing the assisted-PC window twice, keeping it hidden for 21 seconds, restoring it through its actual notification icon, and retaining the session.
- Closing the controller, retaining authenticated heartbeat for 21 seconds, restoring it through its notification icon, and automatically resuming fresh live frames.
- Ending support from the controller while both processes stay alive, denying the old token, starting a new assistance session in those same processes, and then ending support from the assisted PC while both applications remain open.
- Automatic process/files loading, typed sorting, minimum-size navigation, and pairing/connected text bounds at 1060×720 and 1280×860.

The actual Release screenshots were visually inspected, including the live view, minimum-size Processes and Files workspaces, and the assisted PC's **Assistance terminée** screen with **Nouvelle assistance**. Controls remain readable at the tested default DPI; wide process columns retain horizontal scrolling. The narrower navigation rail, consistent buttons, larger viewing surface, and separate input-availability status are visible in these captures.

Earlier diagnostic runs exposed native close messages being classified as `TaskManagerClosing` and an outdated external Lab UI contract. The close policy and its tests were corrected; the contract is now copied alongside the published Lab. The final passing request above used the corrected contract and exact final Release. The initial attempt to stage the entire artifacts directory exhausted a disposable payload volume before application launch; scoped canonical Lab and Release inputs resolved staging without changing the release contents.

The 0.2.1 installer has SHA-256 `0D0F396EAA2871D3E4FD23F50537BF27600237C4D5AD4ACA0A192CBA6F209E15`. Request `executable-test-20260912T090417459Z-c313513b` evaluated it in another disconnected Hyper-V guest and passed. Setup returned zero, ran with `Administrative install mode: No`, installed under the test user's LocalAppData, created the per-user Start menu shortcut and HKCU uninstaller, and logged successful completion without a Windows restart.

For both final requests, broker and guest harnesses report success, application assertions report `TestPassed=true`, process cleanup verified no survivors, and the worker ended Off before asynchronous recycling. No evidence warnings were reported. The disposable payload children were reported deleted and independently confirmed absent on disk; the read-only Release input also reports successful cleanup and an absent child. All network adapters were disconnected. Independent Hyper-V disk-attachment enumeration was denied to the development user's token, so attachment cleanup relies on the broker's successful cleanup report rather than a separate host inventory.

Current scope limits: the three optional blocked checks are LAN/provisioning scope and OS-level power-request acquisition/release, which requires a privileged observer. This loopback run does not qualify physical-PC input delivery, multiple DPI or monitor configurations, the full update matrix, or the real ten-minute expiry under 0.2.1. Earlier release evidence below is historical and does not turn those current limits into new passes. Compilers and unit tests ran on the host; the application and installer ran only in isolated guests.

Reproduce the current focused checks with `./scripts/Test-Unit.ps1`, `./scripts/Build.ps1 -IncludeLab -Sign`, and `./scripts/Test-HyperV.ps1 -Role LoopbackTray -Scope Runtime -UpdateVariant None`.

## Historical evidence — 0.2.0 candidate

The remainder of this document records the previous release and its original lifecycle, including the old agent-exits-on-termination behavior. These results are retained as historical evidence, not current 0.2.1 assertions.

The support workflow overhaul is implemented. Release validation combines pure tests, isolated runtime/UI checks, actual five- and ten-minute timers, and a dedicated administrator-provisioned two-PC update test. Completed evidence and remaining limits are distinguished below.

The [0.1.0 validation record](VALIDATION-0.1.0.md) is historical evidence for the previous interface and protocol. Its passing scenarios do not establish a pass for this candidate. The [acceptance matrix](ACCEPTANCE_MATRIX.md) describes planned checks; it is not a completed test report.

## Build and pure tests

- Windows x64, self-contained .NET 8, Release single executable.
- Release build: zero warnings and zero errors.
- September 11, 2026: **100/100 pure unit tests passed**, with no skipped tests (88 Core and twelve platform tests). They cover framing and path confinement, six-digit pairing and rate limits, PAKE authentication, session timing, screen geometry, typed table sorting, discovery address identity and stable endpoint selection, maintenance lease policy, update policy, service PID attestation, firewall/update deadline ordering, startup-health readiness, and bounded byte progress.
- Authenticode publisher SHA-256: `772169E21DEBE5D4E39D74BE04F168038C539552844CA06F86766A5FAEAD36EC`. This is an explicitly enrolled local publisher, not a publicly trusted certificate. Signing does not install a development-host trust root.

Compilers and pure unit tests ran on the development host. The application, its CLI, fixtures, and integration Lab run only in broker-controlled Hyper-V guests.

## Isolated interface findings

The initial 0.2.0 interface smoke exposed a Windows firewall dialog covering pairing, along with clipped labels and unnecessary inner scrollbars. The agent now remains on loopback until the protected broker confirms Private/LocalSubnet firewall preparation. This keeps pairing visible before the one-time setup.

Request `executable-test-20260911T154100399Z-c97f6194` tested Release hash `8E36014F8DFBCFA0406EA3C8C73F7E5E3F95254645DBF93FEC043AF6A0C46BB9`. Its inspected screenshots showed pairing without the firewall dialog and without the inner scrollbars. It was an **unevaluated interface smoke**, not a support session pass. The controller screenshot exposed further clipped labels, which were subsequently corrected. That earlier hash is not the current candidate.

## Runtime acceptance

Request `executable-test-20260911T162741983Z-01a2132d` exercised signed Release hash `FB8545C318125583909F0BA5641376FAE0C85B014909D9D108CDC0B54BD4F56E`, built from source commit `8d23d25`, in a disconnected guest with two GUI processes on loopback.

The report contains **51 passing checks, three required failures, and five optional blocked checks**. Pairing with Enter, exact running-executable hash equality, automatic fresh viewing, default input enablement, real viewer focus, automatic process/files data, typed sorting, minimum-size navigation, and uninterrupted viewing beyond 310 seconds passed. Existing fixture deployment, UI Automation, file transfer, diagnostics, debugger/dump, cancellation, streaming, path confinement, idempotence, restart, hang/crash recovery, and intervention-history checks also passed.

This was **not an overall pass**: the tray probe selected an app-title element instead of the Explorer notification icon, failed restoration, and then failed when looking for controls in the hidden window. The saved UI Automation inventory confirms the wrong process and the actual Explorer overflow control. The Lab now confines tray lookup to Explorer shell windows and searches actual menu surfaces. A separate `LoopbackTray` role retests pairing, live viewing, tray restoration and termination without repeating the elapsed-time and broad regression checks.

Root visually reviewed connected-agent, live-view, Processes, and Files screenshots at 1060 by 720. The primary controls fit; process columns remain horizontally scrollable where necessary. Earlier blank sleep state, misleading connected footer, clipped live controls and file-toolbar alignment were corrected. The later candidate also labels reconnection explicitly and ties the footer to its visible workspace. This is default-DPI evidence, not a multiple-DPI approval.

Both the broker and guest harness completed successfully for that request. Application evaluation returned `TestPassed=false` for the tray failures. Worker 2 ended Off, process cleanup verified no survivors, all 40 evidence files were copied, no evidence warnings were reported, and the payload child was deleted. Loopback does not establish LAN discovery or GUI input delivery between two independent desktops. Exact hash equality in this run does not establish replacement of a mismatched executable.

The focused tray rerun `executable-test-20260911T174611921Z-c1fb9abe` passed all required assertions with Release SHA-256 `9019DB6BC9AB9F14CD064BA63A1F97C0E6DEC67CAB0134C0116D4B68B0CF4C9A`. Closing kept the controller and authenticated session alive; Explorer's actual notification icon opened its menu; Open restored the window; termination exited the agent and denied the old token. The live screenshot was visually reviewed. Broker and guest harnesses succeeded, the VM ended Off, process cleanup verified zero survivors, the child was deleted and absent from disk and VM attachments, and no evidence warnings occurred. Power and LAN checks were explicitly outside this loopback run. The subsequent service identity fix does not change this UI flow.

## Actual elapsed-time acceptance

Request `executable-test-20260911T161809684Z-a8517312` tested Release hash `00BAEE42251B0A8E53D1E77E205D750D63C219CA89E112B6B81FB15DD24467A6`, built from `14aa39d`. It passed all nine required checks: **ten checks passed and two optional power-request checks were blocked**. The broker and guest harness succeeded, and application evaluation returned `TestPassed=true`.

The visible initial countdown was 04:57. Across 126 unchanged observations, the code remained stable until expiry and rotated after 297.300 seconds. The expired code was denied and created no grant; the newly displayed code paired through Enter on the same endpoint.

The controller process was stopped at 16:24:21 UTC. The agent showed its reconnect countdown 15.187 seconds later, at 09:59 remaining, and exited at 16:34:37 UTC, within two seconds of the observed ten-minute deadline. The run also seeded an unreachable saved connection to check that inactive saved state did not delay agent shutdown.

Worker 3 ended Off; process cleanup verified no survivors, all ten evidence files were copied, no evidence warnings were reported, and the payload child was deleted. Windows denied the medium-user `powercfg /requests` query in this run; the later provisioned run supplies a separate privileged observer. This earlier hash does not validate later private-listener promotion or managed-launch changes; its session timing logic remains unchanged.

## Provisioned two-PC acceptance

The dedicated test image uses an administrator-installed, publisher-pinned broker. The application and Lab run as the registered interactive user at medium integrity. The test network connects only the two disposable guests.

The initial runs verified protected installation, service identity, automatic Private/LocalSubnet firewall-rule creation, discovery on launch, six-digit Enter pairing, the actual Windows system sleep request, and visible automatic administrator maintenance without another prompt. They exposed cold-start platform deadlines and a two-minute product deadline that cancelled a real 168 MB update transfer. A later run completed transfer, signature verification and replacement, then exposed a race between remote reconnect and the startup-health acknowledgement. The current candidate separates authentication and synchronization deadlines and waits for explicit startup readiness. Its compressed executable is approximately 75 MB; both interfaces show actual byte progress. These earlier runs are diagnostic evidence, not an update completion pass.

The next run used controller Release SHA-256 `48CA5077A06C0A4B31E27DD60ED9FA0E900F662C469D2C06A2EE2F9755D6FCA0` and a different signed executable with the same 0.2.0 version label. Requests `executable-test-20260911T185526406Z-9dfe54b6` (agent) and `executable-test-20260911T185526672Z-d6d52106` (controller) verified real transfer, protected replacement, automatic restart, exact running-binary hash equality, automatic administrator maintenance, and fresh live viewing across two independent desktops. Root inspected progress screenshots showing 61% / 44.0 MiB on the controller and 70% / 50.0 MiB on the agent at different capture times, then inspected the connected live view. The controller's follow-up reconnect failed because automatic discovery reselected the same peer and the interface treated that as a target change, ending support. Thus this run proves update completion but **is not an overall controller acceptance pass**. The agent's OS sleep request was observed both acquired and subsequently released.

Both request harnesses succeeded, ended their workers Off before recycling, verified zero process survivors, removed their payload children, and disconnected and removed their request network leases. Child absence and zero remaining attachments were independently checked. The agent evidence transfer retried once and then succeeded; neither terminal result reported evidence warnings.

After correcting discovery reselection, the focused rerun **passed all required assertions on both PCs**: 15 agent checks and 15 controller checks passed. Requests were `executable-test-20260911T191138599Z-9058dd95` (agent) and `executable-test-20260911T191138842Z-6fac9759` (controller), using Release SHA-256 `7DA396687FEB3F6C55877A6DE3BCC469381DCC74084D0C80FF328D0760E19E42` and same-version fixture `5F234FF97D0F17D6B08069E93FBB2660EC01F8D06A1FA8226EE3B0D4479DEB63`. Exact-byte replacement, automatic live viewing, saved-session reconnect across a discovery refresh, close-to-tray with authenticated heartbeat, actual tray Open restoration, explicit termination, agent exit and OS sleep-request release all passed. Root inspected both progress bars and the connected live screenshot. The latter was dimmed by a user-opened screenshot overlay, while the live badge and telemetry remained visible.

Both broker and guest harnesses succeeded with application `TestPassed=true`; workers ended Off before recycling; cleanup verified no process survivors, payload children or attached payload disks; network leases were removed and adapters disconnected. The agent evidence transfer retried once, then succeeded without terminal warnings. Optional first-time UAC, code rotation and expired-code checks were outside this focused run; earlier elapsed-time evidence covers rotation. Subsequent typography changes are assessed separately below and do not change the updater or session lifecycle.

## Final typography and packaged interface check

The user identified unequal text indents and clipped type on the connected-agent screen. The header and agent text now share a text renderer without font-dependent padding, their layout margins align, and heading/subtitle rows use measured text height instead of fixed rows. The 32-point agent heading requires 59 pixels; the previous row allowed 50 pixels before margins.

Request `executable-test-20260911T192255617Z-0d78105e` tested the final signed Release SHA-256 `53A99E5988A4F71B7259DFB8187164CDF6F4FC44A9C5B6E9572A629104FA0D4E` in a disconnected guest. **All required assertions passed; 19 checks passed.** Pairing and connected views passed text-bound measurements at 1060×720 and 1280×860: all six inspected text blocks had the same left edge and enough height for their measured text. Root visually inspected pairing at minimum size and connected views at both sizes, confirming full headings, subtitles and session text. The same run passed Enter pairing, exact executable identity, fresh live viewing, default input/focus, actual tray restoration and termination.

Broker and guest harnesses succeeded, application `TestPassed=true`, worker 2 ended Off before recycling, and process cleanup verified zero survivors. The payload child was deleted, absent on disk and absent from VM attachments; all network adapters were disconnected and no evidence warnings were reported. An early optional live capture requested the connected screenshot before it existed and correctly returned `GuestEvidenceUnavailable`; the later capture and complete terminal evidence succeeded. The final ZIP is assembled from this exact executable. This layout-only run does not repeat the provisioned updater test above.

## Per-user installer

Request `executable-test-20260911T231950149Z-8a3cfa22` tested the signed
`RemoteDebugger-0.2.0-Setup.exe` with SHA-256
`1618F77E9947AE727D98A2470C4E70CF71B4D81F796EB9069EDE72D4F049EF4E` in a
disconnected isolated Windows guest. The installer ran with no user privileges
and `Administrative install mode: No`, returned exit code zero, installed the
application beneath the test user's LocalAppData, created the per-user Start
menu shortcut, and registered the HKCU uninstaller. The declared result-file
assertion passed. Harness and guest execution succeeded, process cleanup passed,
the VM ended Off, the disposable payload child was deleted, and there were no
evidence warnings. This test did not invoke the separate administrator-approved
**Activer sur ce PC** provisioning flow.

## Remaining acceptance boundaries

1. Interactive first-time UAC consent is not automated. The dedicated run used the explicitly administrator-provisioned image and verified the protected application and LocalSystem service identities. No UAC bypass or application execution on the development host was used.
2. The extended upgrade, downgrade, interrupted-transfer, rollback, termination-during-replacement, and privileged crash-cleanup matrix has not been completed. Same-version replacement and the exact executable hash after restart have runtime evidence; policy unit tests do not replace that extended matrix.
3. Automatic Private/LocalSubnet firewall rules were inspected. The disposable isolated network exempts its test adapter from firewall enforcement, so this does not prove enforcement on a physical LAN. Discovery and fresh viewing between separate desktops passed; full GUI mouse/keyboard delivery on independent PCs remains outside the completed focused run.
4. Multiple-DPI visual review and multi-monitor input geometry remain unverified. Minimum-size layout and viewing beyond one five-minute stream connection have evidence as described above.

## Reproduction

```powershell
./scripts/Test-Unit.ps1
./scripts/Build.ps1 -IncludeLab -Sign
./scripts/Test-HyperV.ps1 -Role Loopback -Scope Runtime -UpdateVariant None
./scripts/Test-HyperV.ps1 -Role LoopbackTray -Scope Runtime -UpdateVariant None
./scripts/Test-HyperV.ps1 -Role LoopbackLifetime -Scope Runtime -UpdateVariant None
```

For provisioned two-PC acceptance, prepare the supported administrator-consent guest capability first, build real signed variants with `scripts/Build-UpdateFixtures.ps1`, and use the `Provisioned` or `Full` scopes with the matching update variant. The broker owns workers, payload staging, networking, and cleanup. Do not substitute application execution on the physical host.

Raw reports, screenshots, pairing codes, and session data remain in ignored local evidence directories and broker result storage. The distributable includes public documentation and the public publisher certificate only, never private keys or raw acceptance captures.
