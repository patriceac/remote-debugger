# Validation record — 0.1.0 Release

The final Release passed its application scenario in **one disconnected Windows VM**, using TLS between a controller and an agent on that VM. This validates those application functions within that scope. Complete acceptance between two PCs, GUI input across two independent desktops, and actual elevated maintenance remain open.

## Final build and evidence

- Executable: `artifacts/release/RemoteDebugger.exe`.
- Configuration: Release, self-contained .NET 8, Windows x64, portable single executable.
- SHA-256: `9462400E97374FB94C9FBA8DF0A7F7157ACDAB64CBBBF308F3D7B9384972C68D`.
- Broker request: `executable-test-20260910T224925638Z-5061357b`, completed September 10, 2026 at 22:55 UTC (September 11 at 00:55 Paris time).
- Canonical payload submitted to SYSTEM broker: the project's `artifacts` directory. The lab executable launched the actual Release GUI and CLI inside worker 2.
- Broker and GuestAgent both report `HarnessSucceeded=true`; `TestEvaluated=true`, `TestPassed=true` and application `passed=true`.
- **34/34 unit tests** passed in Release. They exercise pure logic only: framing, path confinement, pairing limits, screen geometry, maintenance lease policy and the 5 fps hard cap. No application-under-test ran on the physical host.
- **44/44 scenario checks** passed, including the explicit scope marker `single_vm_loopback_only_NOT_network_acceptance`.

The maintainer retains the original broker and GuestAgent reports and local records named `evidence/release-validation.json`, `evidence/lab-result.json`, `evidence/unit-tests.json` and `evidence/stream-metrics.json`. Raw reports and screenshots are excluded from this repository because they can contain machine and session details. This document summarizes that historical run; reproduction instructions are below.

The local wrapper initially reported failure after this successful run because it treated the unset/stale PowerShell `$LASTEXITCODE` as the runner's result. It now uses the runner's `ThrowOnFailure` contract. The terminal broker, GuestAgent and application reports establish the pass independently; the product binary was not changed afterward.

## Application behavior evaluated

| Area | Evidence obtained |
|---|---|
| Pairing and access | GUI pairing, CLI reuse, unknown-token denial, wrong-certificate denial, disabled admin session and rejection before local admin consent |
| Deploy and verify | Upload fixture v1, launch, verify running image/hash/file version; repeat with distinct v2 binary |
| Interaction and retrieval | CLI mouse click, Unicode text, key chord, UI Automation save, fresh screenshot, result and log downloads for both versions |
| Debugging | Actual Windows debugger attach, breakpoint exception `80000003`, detach; minidump written |
| Diagnostics | Process CPU/RAM/responding state, system CPU/RAM/volumes, network, services, event log and file listing |
| Commands | Standard-user maintenance command, deadline, explicit cancellation |
| Streaming | Continuous 1080p TLS JPEG stream while commands and input execute; stale screen layout rejection; real GUI presentation |
| Recovery | Persisted pairing after agent restart, upload offset recovery and final SHA-256 match, mutation replay without duplicate launch, new PID after restart |
| Failure and revocation | Deliberate hang detected and recovered, deliberate crash detected, application relaunched, intervention history checked, previous controller denied after revocation |

The hang fixture intentionally recovers after 15 seconds. This is evidence of detection and subsequent response; it does not claim an automatic repair of arbitrary hung applications.

## Stream and rendered interface

The final CLI measurement received **58 distinct frames in 12.017 seconds: 4.826 fps**, at 1920×1080. The estimated application payload rate was **14.66 Mbit/s**, mean capture/encode **38.90 ms**, and inter-frame p95 **223.98 ms**. The controller CPU sample was 1.46% of total machine capacity, and the separate agent sample was 3.82%. These are short samples on a shared host, not LAN throughput or sustained performance guarantees.

The GUI paints before acknowledging each frame. Its inspected screenshot showed **5.1 fps, 16.7 Mbit/s and 47 ms encoding** in its separate short measurement window. Counting the immediately available first frame can put this displayed average slightly above 5; the server schedules subsequent frames at a maximum of 5 fps. The CLI does not measure GUI decode/presentation.

Screenshots recorded as visually reviewed during that run (retained locally):

- `evidence/pilot-live.jpg`: functioning live viewer, explicit navigation and stream status. Recursive preview is expected because both roles share one desktop in this test.
- `evidence/pilot-resources.jpg`: populated process list, timestamps, CPU values and volume information.
- `evidence/remote-v2.jpg`: fixture v2 and its expected saved input visible.
- `evidence/final-desktop.png`: visible agent after revocation, no paired controller, intervention entry confirming revoked access.

## Isolation and cleanup

The final request used `NetworkProfile=None`. Guest process cleanup reports success, verification success, no survivors and no errors. Worker 2 reports final state `Off`; all network adapters are disconnected. The broker reports the disposable payload child deleted. A subsequent read-only filesystem and `Msvm_StorageAllocationSettingData` query confirmed the child is absent and has **zero attachments**. There were no evidence warnings, skipped files or infrastructure retries. Background OS recycling is a separate broker responsibility.

## Two-VM attempt and remaining boundaries

Earlier build `7BB16262CD968A932E900562EEA0D99578E5663FCE036A299C13D9DCD0920CAE` was tested with two concurrent `IsolatedTestNet` requests sharing one cohort. The controller at `10.254.0.103` discovered the agent at `10.254.0.102`; GUI TLS pairing, rejection checks, v1 deployment/running identity and passive UI inspection passed. Foreground interaction then failed while a Windows security/firewall prompt obstructed the desktop. This is historical partial evidence, **not a final-build network pass**.

Requests `executable-test-20260910T215921496Z-537d12a5` and `executable-test-20260910T215921716Z-521b4bf2` are summarized in `evidence/two-vm-limit.json`. One peer subsequently failed the broker check: `The IsolatedTestNet switch has an adapter that is not bound to one active broker lease.` The separate read-only harness diagnosis found lease/adapter revalidation outside the lifecycle mutex; the exact rejected transient state was not captured. The harness infrastructure and its guard were not modified or bypassed.

Both reports record VM Off, adapter disconnection/removal, network lease-state deletion and payload child deletion; both child paths were verified absent. The failed peer has no terminal GuestAgent result, so its process cleanup is **not** attested. These records do not prove final removal of the shared switch. `IsolatedTestNet` exempts its request interface from the guest firewall; connectivity under that profile does not validate the product's firewall installer.

The following remain unvalidated:

1. Complete final-Release operation between two machines, including human GUI mouse/keyboard forwarding, LAN latency, multi-monitor and DPI combinations, and long sessions. Loopback input forwarding from the viewer is deliberately skipped because controller and agent share the same foreground window and input desktop. The CLI input operations did run.
2. Actual local UAC consent and administrator commands. The product provides a visible one-hour helper through `maintenance.session`, a status query, and the per-operation `maintenance.elevated` alternative. The current broker has no elevation/consent contract. Real elevated command completion, cancellation, parent shutdown, revocation and one-hour helper expiry were not exercised; only pure lease policy and denial without authorization were validated.
3. Installation of the product's Private/LocalSubnet firewall rules. A separate inspected prompt was on the Default desktop and requested public/private firewall access; missing UI Automation controls alone did not prove a secure UAC desktop.
4. Application-specific troubleshooting and account workflows on a physical remote PC were outside this recorded test run.

## Reproduction

Run `scripts/Test-Unit.ps1`, then `scripts/Build.ps1 -IncludeLab`. `scripts/Test-HyperV.ps1 -Role Local` submits the disconnected scenario through the SYSTEM broker. Two-machine mode uses concurrent `-Role Agent` and `-Role Controller` calls with the same non-secret `-Cohort`. Respect broker availability and the unresolved validation limits above; never substitute host execution.

The fixture is a small Windows Forms application with editable text, save, an animated clock, deliberate hang and deliberate crash. v1/v2 differ in file version and hash. The lab's test-only UDP bootstrap simulates reading the agent's pairing code and fingerprint; all remote product operations use the actual Release controller CLI. The bootstrap is absent from the distributable executable. Application assertions use atomically written `lab-result.json`, avoiding collision with the GuestAgent's own reserved `result.json`.
