# Validation record — 0.2.0 candidate

The support workflow overhaul is implemented, but full acceptance is not yet complete. Administrator provisioning, actual privileged maintenance, and silent binary replacement still need an administrator-capable isolated Windows test environment. The normal harness launches the application as a medium-integrity user and cannot approve the initial Windows consent prompt. Preparation of a dedicated test environment has been authorized; it has not yet supplied privileged acceptance evidence.

The [0.1.0 validation record](VALIDATION-0.1.0.md) is historical evidence for the previous interface and protocol. Its passing scenarios do not establish a pass for this candidate. The [acceptance matrix](ACCEPTANCE_MATRIX.md) describes planned checks; it is not a completed test report.

## Build and pure tests

- Windows x64, self-contained .NET 8, Release single executable.
- Release build: zero warnings and zero errors.
- September 11, 2026: **82/82 pure unit tests passed**, with no skipped tests. They cover framing and path confinement, six-digit pairing and rate limits, PAKE authentication, session timing, screen geometry, typed table sorting, discovery address identity, maintenance lease policy, and update policy.
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

## Actual elapsed-time acceptance

Request `executable-test-20260911T161809684Z-a8517312` tested Release hash `00BAEE42251B0A8E53D1E77E205D750D63C219CA89E112B6B81FB15DD24467A6`, built from `14aa39d`. It passed all nine required checks: **ten checks passed and two optional power-request checks were blocked**. The broker and guest harness succeeded, and application evaluation returned `TestPassed=true`.

The visible initial countdown was 04:57. Across 126 unchanged observations, the code remained stable until expiry and rotated after 297.300 seconds. The expired code was denied and created no grant; the newly displayed code paired through Enter on the same endpoint.

The controller process was stopped at 16:24:21 UTC. The agent showed its reconnect countdown 15.187 seconds later, at 09:59 remaining, and exited at 16:34:37 UTC, within two seconds of the observed ten-minute deadline. The run also seeded an unreachable saved connection to check that inactive saved state did not delay agent shutdown.

Worker 3 ended Off; process cleanup verified no survivors, all ten evidence files were copied, no evidence warnings were reported, and the payload child was deleted. Windows denied the medium-user `powercfg /requests` query, so OS-level sleep-request acquisition and release remain unverified. This earlier hash does not validate later private-listener promotion or managed-launch changes; its session timing logic remains unchanged.

## Remaining acceptance boundaries

1. One-time Windows administrator setup, protected installation and service identity/ACL behavior, and product Private/LocalSubnet firewall rules.
2. Silent automatic administrator maintenance on connection and cleanup on termination, parent exit, timeout, and interrupted update.
3. Signed upgrades, downgrades, and different builds with identical version labels; exact running-controller-byte identity after restart; interruption, invalid signer/hash rejection, rollback, and termination during replacement.
4. Complete operation across two separate PCs: discovery, fresh viewing, GUI mouse/keyboard forwarding, network disconnect/reconnect, and network boundary checks. No host fallback or UAC bypass is used to fill these gaps.
5. OS-level sleep-request acquisition and release, reconnect before the grace deadline, and cleanup during an interrupted privileged update.
6. Multiple-DPI visual review and multi-monitor input geometry. Minimum-size layout and viewing beyond one five-minute stream connection have evidence as described above.

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
