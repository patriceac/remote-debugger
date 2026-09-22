# Validation record

## 0.5.0 — support workflow (power qualification incomplete)

September 21, 2026: Luna Max ran focused Core/platform regression checks during
implementation. The latest merged-source subset passed **78 platform tests and
6 MCP tests**. The primary agent owns application and test code.

Signed Release `B0892B06C4B39F98BF39583E53EF294863FEA9F7E87F2AA49C89EACFA63C2FD7`
passed clipboard sharing/pause/end, all four Explorer drag-and-drop paths with
nested/empty folders and exact contents, 32 MiB transfer cancellation/resume,
restart preflight, and controller shutdown confirmation/cancellation. The H.264
probe measured 11.06 FPS with a maximum frame age of 175 ms in the isolated VM;
this is not a physical-hardware 30 FPS benchmark. Request IDs:
`executable-test-20260921T174411144Z-54a86e36` (agent) and
`executable-test-20260921T174411217Z-3f6a9c06` (controller).

The controller's overall assertion failed at the final local-report lookup:
the CLI ignored `--data-root`. Commit `2cf3119` fixes that wiring. Separate native
request `executable-test-20260921T180241282Z-f532c410` passed on candidate C613:
an actual unexpected process exit creates an automatic report, readable without
a remote profile, with clipboard payloads excluded. Its screenshot was reviewed;
application, guest/broker, process/payload cleanup and network isolation passed,
and the worker ended Off. Both workflow
VMs ended Off with successful process/payload/network cleanup and no skipped
evidence. The Files completion state and shutdown countdown screenshots were
visually reviewed.

The current application candidate is signed Release **0.5.0**, built from clean
source `207a4025084e7f2a01c03c5d352a6299304fd41e`, with SHA-256
`69DF1BBA0526740BE718CFA56849EFF26900E1856F8CD7E2438EA80A163F05D0`.
It additionally treats NUL-only logon notices as empty while retaining real
interactive banners; Luna's four focused preflight cases passed.
It also puts the stopped reconnect-wait explanation in the visible connection
status. This was visually verified on C457; the latest product change separates
its two sentences with a period. Both installers contain current candidate 69DF:

| Package | SHA-256 | Qualification |
| --- | --- | --- |
| Private setup | `7B1502C81488AFF349968557FD83352150994BE750C39FD0B5984CD67C8E9E32` | Five disconnected installer checks passed; installed executable matched 69DF |
| Standard setup | `6F04795AB5C7C3B43C2ED15CD64C267FDA50F7EB54C46D954AA5E288B2DDCF16` | Built and signed without embedded connection or administrator profiles; no separate install run |

The private package contains protected connection profiles and is kept local.
The publisher fingerprint is
`772169E21DEBE5D4E39D74BE04F168038C539552844CA06F86766A5FAEAD36EC`;
the local signer reports `UnknownError` trust status, so signing is not a claim of
public certificate trust.

Current private-installer request
`executable-test-20260921T223831389Z-a420d956` passed all five checks: fresh
credential state, installation, original-user integration, protected setup and
the first-launch passphrase requirement. The installed executable at
`C:\Program Files\RemoteDebugger\RemoteDebugger.exe` matched the current 69DF
hash above, and setup exited zero. Its first-launch screenshot was visually
reviewed. Guest/broker assertions, process cleanup, payload deletion, disconnected
network cleanup and worker recycle passed without warnings; the VM ended Off.
This silent disconnected check does not qualify Internet connection, a custom
install directory or uninstallation.

Earlier installer request `executable-test-20260921T180221363Z-eb2e7fbd` passed fresh
credential state, silent installation, build identity and original-user shell
integration, protected profile staging, and the first-launch passphrase prompt.
It tested private setup `DC2D9413DDC838413403294F215E03FB53D7C4EF1BC25AE0914EB7D259F385E5`
and installed candidate `C613F0895B8877A268F2782A468E146EE01EC6AC46C414DD5D716C4FCA1FD1B6`
from source `2cf3119ce03b36f42e92c35e49caeafd2fb7d051`. The passphrase
screenshot was visually reviewed. This disconnected check does not qualify an
Internet connection, a custom install directory, or uninstallation.
Guest/broker execution and cleanup succeeded: the VM ended Off, no processes
survived, the payload was deleted, all seven evidence items were copied, and no
network adapter remained connected.

| Requirement | Evidence and remaining limit |
| --- | --- |
| Session clipboard | Focused session/clipboard tests and native bidirectional, pause/baseline and session-end checks passed. Actual reboot remains part of the power qualification gap. |
| Automatic reports | Retention/deduplication checks passed; candidate C613 created an unexpected-exit incident and read it offline without clipboard payloads. |
| Restart and one-use logon | Unit checks, native preflight and cancellation with temporary-login cleanup passed. Actual automatic first boot occurred, but the controller failed to recover the desktop within five minutes. Manual second boot and the full controller wait remain unqualified. |
| Shutdown | Native named-PC confirmation, countdown and cancellation passed. Issued shutdown remains unqualified. |
| Update progress | Ten focused progress tests passed, covering measured/stalled/resumed rates, opaque stages and actual completion; the existing binary/health verification gate is preserved. |
| Smoother viewer | Sixteen stream/frame/acknowledgement checks passed; native H.264 viewing exceeded the former 5 FPS ceiling without a stale-frame backlog. |
| Explorer transfer | Tree/descriptor/destination checks and all four native copy paths passed, including Unicode/nested/empty folders, source preservation and bulk cancellation/resume with exact hashes. |

Earlier attempts exposed an OLE extraction deadlock and Explorer test-timing
issues, corrected before the B089 run. One 0.5.0 provisioning failure did not
reproduce; the CLI now forwards the provisioner's current error, and isolated
request `executable-test-20260921T173912962Z-6cd8e67e` provisioned successfully.

Actual reboot recovery, one-use sign-in/second boot, and issued shutdown remain
unqualified. The Hyper-V Harness project's authorized
Astra Max extension is deployed and Ready: commit
`6414c50a50ebc30f96fdb214601dac422780bdf0`, deployment
`deploy-805bbed3db1ff531`, completed September 21 at 23:56:44 UTC. Its Ready
receipt was independently read. The release passed 498 deterministic scenarios,
eight isolated acceptance paths and a separate network peer. It qualifies
per-boot network restoration, exact terminal-result recognition, setup argument
expansion and bounded failure evidence. Product power qualification is separate.
The test-only power adapter binds the elevated setup to
the exact Lab and Release hashes, retains independent installed-product/service
verification, and checks the target's account/SID/pool-baseline binding before
using the request-private DPAPI credential. Its continuations observe the
product's registered RunOnce launch; its expiry scenario uses the real hour.
Luna's ten
focused provisioning/binding checks, a Windows PowerShell UTF-8 BOM fixture
regression, and the submission script's syntax check passed. All four
`-PrepareOnly` scenarios were regenerated and passed request/plan assertions
against the current Release and Lab SHA-256
`7380CF3F2C26BBF4CFB69605C9724A2DED0BB19311E1077FBC16E7475D97EA0E`:
exact artifact hashes and setup arguments, shared isolated cohorts, credential
fixtures limited to the one-use-login scenario (Manual sign-in uses the broker's
own credential), Automatic/Manual boot ordering, and the
real-hour observation interval. Shutdown explicitly allows 300 seconds for the
qualified harness evidence-recovery path. Prepare-only checks did not run the
application.

The initial Once controller `executable-test-20260921T211717759Z-e7dca8a1`
failed because the Lab looked for cancelled-countdown text in a footer occupied
by viewer controls. The visible session had recovered. Commit `fd368aa` instead
asserts the connected state, unchanged boot and clean temporary-login preflight.
Its dependent target `executable-test-20260921T211717962Z-2b0d3b17` was cancelled;
both requests cleaned up successfully.

On the next Once pair, controller `executable-test-20260921T213040683Z-30b29d04`
passed that cancellation check, then timed out waiting for Desktop Ready after
the first actual automatic boot. Target `executable-test-20260921T213040782Z-7e89c641`
proved a distinct first boot and automatic sign-in without broker credential
input. It did not prove support reconnection or a second actual boot. The target
was cancelled after the controller's definitive failure. Both workers ended Off
with successful payload/network cleanup. This run used candidate 2BBD and Lab
`6E68982B9D12273FFC8666D4FA6EE10432034DA9D8BF08C4F658E4975F064216`.
The controller screenshot was reviewed: Waiting for PC remained visible, with
about 55 minutes left. The deployed harness discarded guest-only continuation
evidence on early failure/cancellation; the replacement now retains a bounded
failure snapshot. Neither account demotion nor OUTDIR reset was found in the harness.

The real-hour Expiry pair ran on C457/Lab 33E8 as requests
`executable-test-20260921T213626249Z-b4c4269d` and
`executable-test-20260921T213626348Z-b6365550`. Broker captures showed the initial
59:49 countdown at 21:42:47 UTC, 38:55 remaining at 22:03:41 UTC, and the stopped
wait with the old desktop removed at 22:43:19 UTC. These screens were visually
reviewed. The controller then failed its four-minute target-response probe at
22:46:51 UTC. The broker observed a distinct manual boot at 21:43:04 UTC,
3700.55 seconds signed out, one managed sign-in, one original launch and no
action replay. The dependent target was canonically cancelled after that failure;
both VMs ended Off with payload deletion and successful network cleanup. This
establishes the visible real-hour timeout, but does not pass the complete
post-sign-in no-reconnect scenario.

The user ruled out repeating the hour-long run. Remaining expiry qualification
uses the preserved real-timer captures, fast injected-clock deadline and expired
ticket checks, and the short native cancellation/reboot path. A complete second
hour-long end-to-end run is not a release gate and must not be started under the
current test plan.
Luna passed all eight session tests and the restart-ticket test in seconds,
including one tick before/exactly at the deadline and rejection of a late
heartbeat or expired restart ticket.

The Shutdown target
`executable-test-20260921T215803298Z-c42a8851` failed elevated setup before the
product ran: the expected-power-off path passed literal `{PAYLOAD}` tokens to
the setup executable. Its dependent controller
`executable-test-20260921T215801617Z-d98c5b76` was canonically cancelled. Both
workers ended Off with payload deletion and successful network cleanup, without
evidence warnings. The replacement harness fixes argument expansion. These
requests used C457 and Lab
`B3B93B963F39C8FFF5A004DF556CDE3CD622138C824FC5666DDFC63546008415`.
The separate Cancel scenario uses the same C457/B3B93 bytes. Controller
`executable-test-20260921T220625028Z-edbd402b` displayed the stopped-wait state and
forgot its connection, then exceeded the Lab's four-minute target-response wait
at 22:17:32 UTC. Target `executable-test-20260921T220625128Z-90fe691a` completed an
actual manual boot at 22:14:09 UTC, with 62.90 seconds signed out, one broker
sign-in and no action replay. Its continuation was only submitted at 22:16:14 UTC
and later reached the request's 30-minute timeout during continuation. Both
requests cleaned up successfully; the controller's overall application assertion
failed and the old harness did not export the target's guest-only diagnostics.
A separately supplied user screenshot showed the target Lab's `power.boot_1`
pass beside a Remote Debugger firewall prompt. That check follows exact-process
identity verification, supporting that the product's RunOnce launch occurred;
the screenshot is user observation, not broker-attested request evidence.

The next Lab allows ten minutes for the post-cancel/expiry target reply and
bounds abandoned target coordination to ten minutes. Neither change alters the
product's one-hour timer. It records the visible stopped wait and removed profile
before probing the returning target, preserving those results on later failure.
Current Lab SHA-256 is
`776A2FFDEA4174F28DF0600A0EEC3224009687F8040E9305398B57FEC282696A`.
Harness canaries subsequently reproduced Public-category drift and loss of the
request-owned Private interface exemption after reboot, with the leased MAC,
address, routes, DNS and IPv6 unchanged. The replacement gate restores only that
request-owned category/exemption before continuation and validates other network
state. Traffic passed to a separate peer after automatic and manual boots.
Autonomous product startup may precede this gate, so the product's firewall-prompt
timing still needs native verification.

After harness qualification, short Cancel controller
`executable-test-20260922T000006133Z-8b5b1bac` passed its stopped-wait check, then
failed because the reported boot identity had not changed. Target
`executable-test-20260922T000006316Z-4c01c79b` independently completed a manual
reboot, one sign-in after 63.62 seconds signed out, a successful network gate and
autonomous product startup at medium integrity. The bounded failure snapshot
retained both phases: each reported GUID `91dc39e4943711f1976c8c65631d8117` despite
the actual reboot. This GUID prevented the product from restoring its restart
grant. The fix queries the documented OS boot time instead; the focused store
test also confirms that unavailable boot information refuses restoration. Luna's
targeted test passed. Both requests cleaned up successfully after cancellation
of the orphan target. No additional hour-long run is planned.

The concurrent Shutdown pair (`executable-test-20260922T000157359Z-b1b0e56c` and
`executable-test-20260922T000157433Z-b24af831`) was cancelled before any shutdown
because a worker readiness failure left its target queued. Cleanup passed; the
worker recovered automatically. This is not a product shutdown result. See
[support workflow](SUPPORT_WORKFLOW.md). Raw evidence is retained
under `D:\Disk\VMs\Codex-Harness\Live\Broker\Results\<request-id>`.

## 0.4.13 — audit fixes

September 19, 2026: Luna Max executed **172 Core tests, 141 Windows platform tests
and 8 relay tests**, all passing; TypeScript checking also passed. Coding and test
implementation were performed by the primary agent.

Isolated request `executable-test-20260919T032611390Z-3a09c462` passed all **57
required application checks** on signed Release
`F925D58A07DEA6BDB0C624EA1E8C0E83831E60671C78249C101BB7EE20311928`.
Both 32 MiB binary transfers resumed after cancellation at a 2 MiB offset and
verified exact hashes. The remaining upload took 1.33 seconds and download 0.19
seconds in a single VM over loopback; these are not physical LAN/WAN benchmarks.
An unchanged adaptive stream emitted one image, then a second after explicit
refresh. Five-minute renewal, stale geometry rejection, hang/crash recovery and
diagnostic/debugging workflows passed. Broker and guest harnesses succeeded,
the worker ended Off, process/disk cleanup passed and no warnings were recorded.
Later changes cover broker capability gating, input-helper teardown and installer
recovery; that runtime result does not qualify those later changes.

Installer request `executable-test-20260919T033254705Z-f0d4c6c7` had a successful
harness and cleanup but failed its application assertion: Windows refused the
initial elevation with error 1223 before installation. Provisioned input request
`executable-test-20260919T034056686Z-81f63df3` was not evaluated because the protected
broker configuration disables `RemoteDebuggerProvisionV1`. Consequently elevated
Task Manager clicks, service refresh/rollback, and the new installer path/user/
uninstall checks remain unqualified at runtime. Their focused Lab checks are
available for a broker with the required capabilities. No host execution fallback
or broker configuration change was made. Raw evidence is retained under
`D:\Disk\VMs\Codex-Harness\Live\Broker\Results\<request-id>`.

## 0.3.0 — Windows startup and VM handoff

September 17, 2026: **214 .NET unit tests pass** (154 Core, 60 platform). The installer now creates a current-user Windows Startup shortcut with `--startup`; that launch stays in the system tray and does not enable support. A normal Start menu launch continues to open the window. The signed Release executable has SHA-256 `0E1E8093C97ECA7304C81D3FBD7486B9EB4E490715D1649CF9F19980D3C83B5F`, and the private installer has SHA-256 `9EBF7F1E7C784812D6782ABFAA6F1DDC183817BFEDA59CE3A1E573BB9A7DCB71`.

Handoff request `executable-test-20260917T191416382Z-74567fc2` installed that exact private package in **Codex-Harness-02** with the broker's approved `InternetOnly` profile. Its application-produced result passed all six checks: fresh settings, installation, DPAPI-protected private settings, plaintext-profile cleanup, desktop integration, and the installed app open for user handoff. Fresh broker-mediated evidence showed the request and application still running when handed to the user. This is live handoff evidence; final process, disk and network cleanup occurs when the user exits or the broker's two-hour deadline expires and is not claimed here.

## 0.3.0 — code-free private support and host-to-VM demonstration

September 17, 2026: **213 .NET unit tests pass** (154 Core, 59 platform), along with **five Worker tests**, TypeScript checking and the Worker build. New coverage checks private authentication without a displayed code, wrong-secret rejection, single-use and revoked grants, protection of both installer secrets, session-bound secret derivation, embedded-profile cleanup, and authenticated discovery including accented computer names and registration ownership.

The final signed Release executable has SHA-256 `8D73AD3C6C324CB347DD3D245D7145F66E60F871A2CF5F6421E05445B71555E7`. The final personal installer `RemoteDebugger-0.3.0-Private-Setup.exe` has SHA-256 `EA56F077AAACB916A3CD7DF016E8059F4688BEF3F1E3ACFD866101BD3C3243AF`. It embeds a relay credential and a separate private authentication key, imports them into DPAPI CurrentUser storage and deletes the extracted plaintext profile. Neither secret nor the private installer is committed or published. Cloudflare deployment `2443d7b8a6024d81afd7129938fde4cd` adds authenticated computer-name discovery at the existing relay and preserves its relay credential; the private authentication key is not uploaded to Cloudflare.

Final installer request `executable-test-20260917T183648039Z-2b776fe9` passed **all seven checks** against those exact hashes: fresh configuration, silent installation, protected settings, plaintext-profile cleanup, Start menu/uninstall integration, initial inactive GUI with no support ID/code/setup controls, and discoverability by computer name after explicit activation. The initial and enabled French screenshots were visually reviewed at 1060×720. This automated test uses the explicit `--enable-support` launch option after checking the normal first-launch screen; it does not substitute for the user-mediated Windows approval demonstrated below.

Real host-to-VM demo request `executable-test-20260917T182839905Z-5b3e6683` passed **all seven checks**. The user clicked **Activer l’assistance** in **Codex-Harness-02** and handled its Windows administrator prompt. The physical host, explicitly authorized as the controller, discovered **CODEX-W11** through the public relay, connected without entering an ID or code, displayed its live desktop at about 5 FPS, and ended support. The VM confirmed the connected and ended states. This used Release `1BD416151A009AA08233AAF8F59D38A0CD6D45A362EE70C150DE2DCF89817D7C` and installer `B6737ECFEFC73A3DDEEDDB343758F248EA5159E9B9253F679A4BAC4BF5956075`; only final connection-screen wording and column width changed before the final installer qualification above. Host evidence is retained in `artifacts/internet-demo/host-to-vm-live.jpg`. The host was subsequently updated to the final Release and its installed executable hash matched; a final foreground recapture was unavailable because Computer Use reported `foreground window did not report a process id`.

Transport request `executable-test-20260917T182431549Z-27dffc6f` passed **all ten application checks** on the preceding code-free Release `79CF024F78088CF8BDBFC9E37D4A72CB737159D8A785BB1779FA74E1BA1FC9E4`: private discovery, rejection of a different installer authentication secret even with a valid relay credential, GUI connection without codes, certificate pinning, exact executable identity, sustained streaming, byte-exact 512 KiB upload/download, bad-pin rejection and grant revocation. Its CLI stream received 57 frames in 12.01 seconds (4.74 FPS), with a 223.1 ms inter-frame p95 and 7.39 Mbit/s of encoded application payload; these figures exclude GUI decode/presentation. This request is **not an overall acceptance pass** because its broker reported `HarnessCleanup` failure. The pool subsequently recycled the worker, and its payload and both input children were independently verified absent.

The final installer and real-demo requests both had successful broker and guest harnesses, evaluated/passing application assertions, process cleanup with no survivors, Off workers, deleted and independently absent payload/input children, successful input cleanup, removed network leases/adapters, all final adapters disconnected, and no evidence warnings. Tests used the broker's approved `InternetOnly` profile and immutable read-only inputs. Hyper-V attachment cleanup is broker-attested because the development token cannot enumerate attachments. The host-to-VM demo proves separate endpoints over the public relay, but does not establish two-site ISP or corporate-proxy compatibility. Raw evidence remains under `D:\Disk\VMs\Codex-Harness\Live\Broker\Results\<request-id>` outside the repository.

## 0.3.0 — preconfigured private installer

September 17, 2026: **208 .NET unit tests pass** (151 Core, 57 platform). The added profile-import regression checks that imported settings are protected for the current user and that an invalid replacement preserves the existing configuration. This packaging change retains the previously tested Release executable, SHA-256 `74BD0AACFB48DED289194E2A4853A642BC7AADCF9FF08305864FA8AFEC210806`.

The signed personal installer `RemoteDebugger-0.3.0-Private-Setup.exe` has SHA-256 `75F1777F22F6981456D9004E8F18B952F9481D14639E40F490C6116376751EDE`. It embeds the ignored local `.rdrelay` profile, imports it through the installed Release CLI before first launch, saves the settings with DPAPI CurrentUser and deletes the extracted plaintext profile. The installer contains the relay credential and remains a private local artifact; it does not contain the publisher's signing private key. `-WithoutInternetProfile` retains the credential-free packaging option.

Request `executable-test-20260917T173818998Z-f3ec4fd3` passed **all six assertions**: a fresh profile before installation, successful silent installation, readable DPAPI-protected configuration, removal of the plaintext profile from temporary and installed files, per-user Start menu/uninstall registration, and first-launch registration with the live Cloudflare relay without a separate profile file or manual import. The installed executable matched the Release hash above. The installation log confirmed no administrator privileges, successful import and no required restart. The final French screenshot at 1060×720 was visually reviewed and showed **Internet ready**, a support ID, public IP and current authorization code. Interactive installer-page rendering was outside this silent-install check. Pairing and transport behavior retain the preceding Release's evidence below.

The test used the broker's approved `InternetOnly` profile and the canonical private installer as one immutable read-only VHDX input; no loose relay profile was supplied to the guest. Broker and guest harnesses succeeded, application assertions were evaluated and passed, process cleanup verified no survivors, and worker 1 ended Off. Payload and input children were deleted and independently absent on disk; input and network cleanup succeeded, the network lease and adapter were removed, all adapters were disconnected, and evidence warnings were empty. Independent disk-attachment enumeration is unavailable to the development token, so that part of cleanup relies on broker evidence.

## 0.3.0 — private internet discovery and transport

September 17, 2026: **207 .NET unit tests pass** (151 Core, 56 platform), along with all **four Worker tests** and TypeScript checking. Added coverage checks support-ID parsing, HTTPS settings validation, bounded encrypted frames, unexpected frame rejection, ordered connection closure, transport error recovery, relay authentication, invitation ownership and replacement-registration isolation. The Worker dependency audit reports no known vulnerabilities.

The signed Release executable has SHA-256 `74BD0AACFB48DED289194E2A4853A642BC7AADCF9FF08305864FA8AFEC210806`. Cloudflare deployment `4dfaff70d6414a50b2951526b8c91fef` serves the private relay at `remote-debugger-relay.pmelekian.workers.dev`; its public health endpoint returned the expected protocol. No paid subscription was enabled. Billing-subscription inspection was unavailable to the connector, so this record does not assert the account's subscription tier.

Internet request `executable-test-20260917T172040055Z-e91f48ce` passed **all ten assertions** using the actual signed Release GUI and CLI. It imported the private settings into two isolated data roots, registered a support ID, rejected a wrong code, paired through the GUI, saved the authenticated endpoint pin and relay route, verified exact executable identity, displayed a live desktop, transferred 512 KiB in both directions with byte-for-byte equality, rejected a false certificate pin and revoked the old grant after ending support. The CLI stream delivered 57 distinct 1920×1080 frames over 12.01 seconds, or 4.74 FPS, with a 222.5 ms inter-frame p95. Its 7.46 Mbit/s figure describes encoded application payload and excludes GUI decoding and presentation.

The broker used its approved `InternetOnly` profile and two immutable read-only VHDX inputs: the canonical Release directory and private setup-file directory. The agent used a normal launch; the controller used the existing separate loopback-only instance scope while its outgoing connection explicitly used WSS. This exercises the deployed public relay from two processes in one Windows guest; it does not establish two-site ISP, corporate proxy, privileged maintenance or executable-replacement behavior. The final French agent, live-controller and ended-session screenshots were visually reviewed at 1060×720. Support ID, public IP, code and session state were visible; the agent page retains scrolling at that size.

The first diagnostic run stopped because the second normal GUI correctly activated the existing single instance. The next reached live viewing but exposed premature socket abortion after a large reply. Async transport disposal now completes a bounded WebSocket close handshake so the last encrypted records arrive. The final passing request above supersedes both runs.

Installer request `executable-test-20260917T172216700Z-ece7d2eb` passed for `RemoteDebugger-0.3.0-Setup.exe`, SHA-256 `A01C49764D738730EE21C61EBF55BD8F71233DDD6109FDD76866611F0C0F79CE`. Silent installation exited with code zero, ran without administrator privileges beneath LocalAppData, created the Start menu shortcut and HKCU uninstall registration, and required no restart. Interactive wizard rendering was outside this check.

Both final broker and guest harnesses succeeded, their application assertions were evaluated and passed, process cleanup succeeded, and workers 1 and 2 ended Off. Payload and host-input children were deleted and independently absent on disk. The internet network lease and adapter were removed, all final adapters were disconnected, and there were no evidence warnings or harness retries. Host attachment enumeration is unavailable to the development token, so attachment cleanup relies on broker evidence. Private setup files, endpoint grants, screenshots and raw acceptance logs remain outside the repository and distributable.

## 0.2.6 — saved language selector with immediate application

September 13, 2026: **196 unit tests pass** (151 Core, 45 platform). Added coverage verifies saved language round trips, returning to System default, corrupt/missing preferences, launch-override precedence, asynchronous work completing in the newly selected language, explicit caption bindings, raw result preservation and column widths. The visible sidebar selector applies English, French, Spanish or System default immediately and saves the choice; it does not restart the app or end support.

The signed Release executable has SHA-256 `82813124F0CDD03D46C45FEFC4BA5708FE2468FCD623BE085699D0B87BFED5A6`. The signed installer is `RemoteDebugger-0.2.6-Setup.exe`, SHA-256 `13285736B3C8AF021106673A03DDA38A730BF23F0662EF9C5F37640BCBF44391`.

Single-instance regression request `executable-test-20260913T133937138Z-756e91e8` passed all eight assertions against that exact Release, including concurrent launches, preserved state, tray/foreground restoration, CLI coexistence and restart after normal Quit or process termination. Installer request `executable-test-20260913T134018688Z-7ddcb6ee` passed against the installer above: non-administrative per-user installation, Start menu shortcut, HKCU uninstall registration and no Windows restart.

Localization regression request `executable-test-20260913T133818764Z-44e3dc7f` passed all 140 checks against the exact Release. It covers the Windows system default, `fr-CA`, `es-MX`, `en-GB` and `de-DE` fallback, all five tabs, pairing, measurements, files, invalid JSON, live/paused/ended states and tray menus at 1060×720. Final screenshots were reviewed, including the Spanish process/file layouts, French viewer and English fallback pairing screen with the new selector.

Final language-selection request `executable-test-20260913T140314781Z-232e0066` passed **all 42 assertions** against the same signed Release hash. The native selector changes French, Spanish and English in the existing processes while preserving the authenticated session and established stream TCP connection. It also preserves pairing codes, typed addresses, diagnostic JSON/original results and file destinations; updates paused/ended states and actual tray menus; restores the saved French choice after normal Quit/relaunch; and remembers a return to System default. Rendered evidence confirms the selector fits the minimum window and the Spanish diagnostic editor retains its technical result.

All four final requests have broker and guest harness success, evaluated/passing application assertions, successful process cleanup with no survivors, final workers Off, all adapters disconnected, no evidence warnings and no infrastructure retries. Disposable payload children and read-only Release input children were reported deleted and independently verified absent; input cleanup succeeded. Evidence is retained under `D:\Disk\VMs\Codex-Harness\Live\Broker\Results\<request-id>`. Attachment cleanup is broker-attested because the current token cannot independently enumerate Hyper-V attachments. This qualification covers the disconnected Windows 11 guest at 100 percent scaling and 1060×720; it does not repeat the wider DPI, LAN or protected-service/UAC matrix. The previously running host application was restored using the canonical 0.2.6 Release after the build, without host application testing.

Exploratory selector requests `executable-test-20260913T133808449Z-a9e23c3b` and `executable-test-20260913T134304637Z-bd84445e` are not acceptance passes. Their screenshots show successful immediate language changes and an active English live session, but the new test initially retained its previous async culture and then compared a composite status-pill accessibility value with a single caption. The Lab now uses a shared expected language, the existing status-prefix convention and telemetry matching for all three languages. No product change was needed for these test corrections.

Selector request `executable-test-20260913T134826178Z-0fd650aa` passed 31 assertions, including immediate French/Spanish/English switching with unchanged process IDs and established stream connections, preserved diagnostic input/output and file destination, and paused-state translation. It then failed to open the assisted-PC language dropdown while that window was behind the controller. The selector helper now explicitly focuses the intended process first. This incomplete run is not a full acceptance pass.

Selector request `executable-test-20260913T135354122Z-85999f20` passed 33 assertions, including the ended assisted-PC state changing to English. It stopped because the new Lab role still used the legacy French tray-role selector. Its retained UIA inventory confirms the expected English Assisted PC icon was present beside the French controller icon. The Lab now uses the selected language for this role, and failed tray screenshots no longer try to focus a hidden main window. The signed product remained unchanged throughout these test-helper corrections. All exploratory requests ended with successful harness/process cleanup, no surviving processes, workers Off, disconnected adapters, no broker evidence warnings and payload/input children deleted and independently absent.

## 0.2.5 — interface languages and one desktop instance

September 13, 2026: **177 unit tests pass** (135 Core, 42 platform). Added coverage verifies Windows UI-language selection, French/Spanish regional variants, English fallback, complete translation catalogs, format placeholders, asynchronous UI culture, preserved regional formatting, installer language detection and desktop-instance identity.

The signed Release executable has SHA-256 `8EE13F2EC4B49D55595597991A47A14651BD74D646762720AF76ADD81F0BFF42`. The signed installer is `RemoteDebugger-0.2.5-Setup.exe`, SHA-256 `5E41022F1E701F264CD40F87C5E4E16D3EB46C510E6C9F9425B1C5393F22118F`.

Localization request `executable-test-20260913T124459464Z-37301469` passed **all 140 checks** against that exact Release. It exercised the unmodified guest system language (French), `fr-CA`, `es-MX`, `en-GB` and unsupported `de-DE` (English fallback). Each case covered the assisted-PC view, all five controller tabs at the minimum 1060×720 window size, invalid pairing, an authenticated loopback session, process measurements, file controls, invalid diagnostic JSON, pause/resume, session termination and translated tray menus. Actual final screenshots were reviewed for French process measurements, Spanish pairing/processes/files/diagnostics, English pairing/files, German-to-English fallback and single-instance restoration. Longer translated labels fit the tested layout; process-table content retains horizontal scrolling.

Single-instance request `executable-test-20260913T124509823Z-dc8af7c3` passed **all eight checks** against the same Release. Normal GUI launches share an identity per Windows user/session, including launches with different data directories or language arguments. The run verified preserved address/language, CLI coexistence, three concurrent duplicate launches while the primary was hidden in the tray, restoration to the foreground, normal Quit/relaunch and recovery after terminating the owner. The production test uses the normal desktop identity. Separate loopback-only Lab data roots remain available to represent two PCs inside one guest.

Installer request `executable-test-20260913T124646585Z-acad3925` passed against the exact installer above. Silent installation exited with code zero, ran without elevation beneath LocalAppData, created the per-user Start menu shortcut and HKCU uninstall registration, and required no restart. The compiled installer includes English, French and Spanish with Windows UI-language detection and English fallback; interactive wizard rendering was not evaluated by this silent-install scenario.

For all three final requests, broker and guest harnesses succeeded, application assertions were evaluated and passed, process cleanup succeeded, and the workers ended Off. Disposable payload children were deleted and independently confirmed absent. Both Lab runs supplied the canonical Release directory as a read-only VHDX input; their input children were deleted and independently absent, with successful input cleanup. All network adapters were disconnected, evidence warnings were empty, and no retries were needed. Independent host disk-attachment enumeration remains unavailable to the development token, so attachment cleanup relies on broker evidence.

Evidence is retained under `D:\Disk\VMs\Codex-Harness\Live\Broker\Results\<request-id>`, including `lab-result.json` and the `localization-*.png` or `single-instance-restored.png` captures. Reproduce with `./scripts/Test-Unit.ps1`, `./scripts/Build.ps1 -IncludeLab -Sign`, then `./scripts/Test-HyperV.ps1 -Role Localization` and `./scripts/Test-HyperV.ps1 -Role SingleInstance`. This qualification covers disconnected guests at 100-percent scaling and loopback sessions; it does not requalify every DPI, physical LAN behavior or the full provisioning/update matrix.

Earlier localization request `executable-test-20260913T123336995Z-ec92455e` was cancelled while the Lab's tray lookup and the additional single-instance feature were being completed. It is not an acceptance pass. Its guest ended Off, payload/input children were deleted and independently absent, adapters were disconnected, and evidence warnings were empty. The earlier passing installer request `executable-test-20260913T123450652Z-f03f7707` was superseded by the final installer above.

## 0.2.4 — clipping, table preferences and session-end exit

The user's screenshots exposed vertical clipping that the 0.2.3 window-bounds assertions did not detect. The revised layouts use content-sized rows for process metrics and toolbars, shared vertical alignment for mixed native controls, and single-line field labels. The PC, process and file tables now persist independent widths and display order by stable column ID, using logical pixels across DPI changes. The assisted PC starts a visible ten-minute exit countdown after access is revoked; new assistance cancels it, while the controller stays open.

The current signed Release is SHA-256 `D39FF8E5D0BC297EC39F15BB1FE5715DE958475751B6C0D986AC513338290ACC`. **145 unit tests pass** (111 Core, 34 platform), including preference round trips, corrupt JSON, added/removed/duplicate columns, and monotonic countdown start, expiry, cancellation and duplicate notifications.

Full UI request `executable-test-20260912T143744993Z-0fa27c40` passed **126 checks with zero failures** against this final Release. All five tabs passed at 100/125/175/150 percent, including toolbar centers, containment within ancestor panels and metric text height. Actual screenshots from the final executable were reviewed for all five tabs at 100/150 percent and representative process/file layouts at 125/175 percent. Native header resize/reorder, DPI retention, both tray lifecycles, dropping stale frames, input preference, diagnostic/file recovery, session termination, a second authenticated session, countdown cancellation and restored columns after Quit/relaunch passed. All five restarted tabs also passed at 150 percent. Four optional checks are explicitly blocked: the absent 200-percent preset, LAN/provisioning scope and privileged OS power-request acquisition/release; coverage is not claimed complete beyond that scope. Reproduce with `./scripts/Test-HyperV.ps1 -Role LoopbackUi`, `-Role LoopbackColumns` and `-Role LoopbackExit`; none changes production timeouts.

This final UI run's broker and guest harnesses succeeded, application assertions passed, process cleanup found no survivors, worker 1 ended Off, payload/input children were deleted and independently absent, host-input cleanup succeeded, all adapters were disconnected, and evidence warnings were empty.

Focused startup/columns request `executable-test-20260912T143720651Z-3bc027ee` passed **23 checks with zero failures** against this final Release. All three tables retained their native resize/reorder gestures through an actual change to 150-percent scaling, session termination, a second authenticated session, Quit and a new process using the same data directory. All five restarted tabs passed shell, alignment and containment assertions at 144 DPI; their actual screenshots were reviewed and show the corrected startup layout. The assisted-side exit countdown was also visible. Broker and guest harnesses succeeded, application assertions passed, process cleanup found no survivors, worker 2 ended Off, payload/input children were deleted and independently absent, host-input cleanup succeeded, all adapters were disconnected, and evidence warnings were empty.

Real-time exit request `executable-test-20260912T143745242Z-732ed932` passed all three assertions against the final `D39FF8E5…` Release: a real authenticated session, the visible 09:58 countdown after ending it, then normal exit (code 0) of the assisted process after another 597.58 seconds while the controller stayed open. The countdown ran in the tray with the production ten-minute timeout. Broker and guest harnesses succeeded, application assertions passed, process cleanup found no survivors, worker 3 ended Off, payload/input children were deleted and independently absent, host-input cleanup succeeded, all adapters were disconnected, and there were no evidence warnings. The initial `49A2DD14…` candidate also passed this full-duration scenario in request `executable-test-20260912T133323629Z-5905b966`.

The rebuilt signed installer is `RemoteDebugger-0.2.4-Setup.exe`, SHA-256 `62607B3863BD89B77FB5147F21F6D10CACA8F57A94F88BB1D18504E3F4015796`. Request `executable-test-20260912T143949774Z-eef27d92` passed for this final package, including the updated documentation and startup-DPI fix. Installation ran without elevation beneath LocalAppData, created the Start menu shortcut and HKCU uninstall registration, and required no Windows restart. Broker and guest harnesses succeeded, application assertions passed, process cleanup found no survivors, worker 4 ended Off, and the payload child was deleted and independently absent. All adapters were disconnected and evidence warnings were empty. Earlier installer candidates passed requests `executable-test-20260912T132432219Z-157261f9`, `executable-test-20260912T135839774Z-28d17f81` and `executable-test-20260912T140242861Z-d4d65b10`, but were superseded by documentation or startup-DPI changes.

Qualification uses disconnected Hyper-V guests and real loopback sessions. It does not establish physical LAN latency/input, multiple-monitor behavior or the full administrator/update matrix. The guest exposes 100/125/150/175-percent presets; 200 percent is an explicit optional capability limitation. LAN/provisioning and privileged OS power-request assertions remain outside this runtime test scope. Independent host disk-attachment enumeration is unavailable to the development token, so attachment cleanup relies on broker evidence; child-file absence is independently checked.

Exploratory evidence is not acceptance: request `executable-test-20260912T131220906Z-14e4d8eb` stopped because newly added test selectors were missing from the test contract. Its guest application assertion failed; its broker also reported a host-input cleanup error. Request `executable-test-20260912T131505825Z-19d8ef8e` exposed wrapped file-row captions and showed that disabling the PC list also disabled its headers. Native test gestures and evidence serialization were tightened after this run. It and superseded requests `executable-test-20260912T131856115Z-da41bf8d` and `executable-test-20260912T132023614Z-14eb920b` were cancelled through the broker; each reported an Off guest, deleted payload/input children, no remaining child files and zero evidence warnings.

Request `executable-test-20260912T132316926Z-ffcd5015` passed alignment and ancestor-containment checks on all tabs at 100/125 percent, plus native column dragging/saving. Its width comparison incorrectly used UIA caption bounds (which omit theme padding), and its preset lookup stopped at 200 percent. The Lab now reads `LVM_GETCOLUMNWIDTH`, leaves live input before operating Settings, and explicitly focuses Settings. The initial failed run's payload/input children were subsequently verified absent after broker recovery.

The subsequent selector investigation distinguished a XAML popup/process mismatch from a display capability limit. UI request `executable-test-20260912T133323430Z-0023125a` passed all three header drag/save checks, then failed to find a preset because it filtered popup items by the hosting window's PID. Focused Settings probe `executable-test-20260912T134100275Z-fa182e3d` fixed that lookup and captured the actual preset inventory: **100, 125, 150 and 175 percent**. The 1920×1080 guest does not expose a 200-percent preset; the 200-percent rendered UI remains unqualified. The final UI run records this as an explicit optional capability limitation and checks every available preset. Settings screenshots now leave Settings focused, rather than bringing the product in front of its popup.

Focused Settings request `executable-test-20260912T134726158Z-c3deccf6` passed all available preset/DPI assertions and explicitly blocked the absent 200-percent preset. Broker and guest harnesses succeeded, application assertions passed, worker 3 ended Off, process cleanup found no survivors, and both payload/input children were deleted and independently absent. All network adapters were disconnected and evidence warnings were empty.

UI request `executable-test-20260912T134431754Z-dfb21282` passed 112 checks, including all five tabs at 100/125/175/150 percent, every table's native resize/reorder and DPI retention, a second authenticated session, and exit-countdown cancellation. Actual rendered screenshots at all these scales were reviewed. The run then failed at the Lab's tray-menu lookup before controller restart, so it is not a complete application pass and does not prove restart persistence. The restart step now hides the controller first, waits for the Quit item, and restricts menu lookup to the intended process. This failed run cleaned up successfully: broker/guest harnesses succeeded, worker 2 ended Off, process cleanup succeeded, payload/input children were deleted and absent, all adapters were disconnected, and warnings were empty.

The repeated broad run `executable-test-20260912T140018118Z-200cd1f5` again passed 112 checks before the same tray lookup failure. Focused requests `executable-test-20260912T141529104Z-7bc4f14a`, `executable-test-20260912T141928689Z-28567893` and `executable-test-20260912T142452871Z-2f3aacb1` isolated a Windows notification covering the icon while UIA still marked that underlying icon visible. XAML's point lookup also returned a different fragment after the toast disappeared. The Lab now uses physical-window hit testing, waits for the obstruction to clear without hovering it, and scopes menus to the intended process. Each failed request had successful broker/guest cleanup, an Off VM, absent payload/input children, disconnected adapters and no evidence warnings.

Focused request `executable-test-20260912T143044902Z-61cabc47` then passed all 13 column/session assertions, including actual Quit and restored widths/order in a new process at 150 percent. Its screenshots exposed another product defect: starting directly at increased DPI scaled fonts before later-built layout panels. The constructor now suspends layout until all controls have been built. The final test additionally checks shell geometry and all five tabs after restart; the earlier passing column-only result is not acceptance for startup layout. Its broker/guest harnesses and application assertions passed, worker 1 ended Off, process cleanup found no survivors, payload/input children were deleted and absent, adapters were disconnected and warnings were empty.

## 0.2.3 — complete workspace UI review

Final Release request `executable-test-20260912T115849551Z-08da347e` **passed all required assertions: 55 checks passed, zero failed**. All five tabs were exercised and their actual screenshots reviewed at 100 percent (1060×720) and 150 percent (1590×1017), covering disconnected, connected and ended-session states. Additional checks covered invalid pairing, diagnostic templates/JSON/errors/cancellation, invalid-directory recovery, paused viewing, input preference, dropping stale frames, both tray lifecycles, and a second session in the same processes. Windows Settings actually selected 150 percent and the Release reported 144 DPI. The assisted-PC explanation was visibly reachable by scrolling, with its bottom at y=912 above the footer at y=950.

Broker and guest harnesses succeeded, application assertions passed, worker 4 ended Off, and process cleanup verified no survivors. Payload and read-only Release-input children were reported deleted and independently absent on disk; input cleanup succeeded, all adapters were disconnected, and there were no evidence warnings. Three optional checks remain blocked: LAN/provisioning scope and privileged OS power-request acquisition/release. Independent host disk-attachment enumeration is unavailable to the development token, so attachment cleanup relies on broker evidence. This run does not qualify physical LAN latency/input, multiple monitors, 125/200 percent scaling, or the full administrator/update matrix. Reproduce with `./scripts/Test-HyperV.ps1 -Role LoopbackUi -Scope Runtime -UpdateVariant None` after the signed Release/Lab build.

September 12, 2026: **140/140 unit tests passed** (109 Core and 31 platform). Thirteen added cases cover live-session control availability, file selection and cancellation, tab-specific footers, authenticated identity, and diagnostic argument templates/PIDs.

The signed Release executable has SHA-256 `5982A66891C0A4CE3F9902B9D34A93A6D30C6052CC95EF26799E056A51C7E877`. The UI now disables pairing during a session and remote actions before a healthy connection, keeps each tab's status separate, initializes Diagnostics templates and labels, reports execution/cancellation/errors, and clears old results and selections after support ends. Windows DPI changes no longer let the image's preferred width or a long connection message expand content beyond the window. The assisted-PC workspace has an explicit scroll extent so its final explanation remains reachable on shorter displays.

The review explicitly inspected Connection, Remote screen, Processes, Files and Diagnostics. The first exploratory request (`executable-test-20260912T113330603Z-63a97628`) stopped at a Lab dropdown interaction: UI Automation selection did not close the WinForms popup, so the next lookup searched the popup. The Lab now collapses it explicitly. The next exploratory request (`executable-test-20260912T113842938Z-299d251e`) reached actual 150 percent Windows scaling and exposed viewer overflow and a long post-session Connection message extending beyond the window. Visual review also found a stale Diagnostics prompt after pairing. These findings led to product fixes and stricter assertions. A superseded request (`executable-test-20260912T114444279Z-526addf9`) was cancelled through the broker before the final run. Each of those guests ended Off, with disconnected adapters, no evidence warnings, and disposable payload/input children reported deleted and independently absent on disk. Exploratory failures and the cancelled request are not acceptance passes.

Candidates `executable-test-20260912T114622392Z-93e761be` and `executable-test-20260912T115047854Z-a98fc666` passed their automated checks, but visual review found the agent explanation clipped below the footer. The final assertion now requires both the text to fit the scroll viewport and that viewport to fit above the visible footer; the product also constrains the parent layout row. This prevents an oversized off-window panel from producing a false visual pass.

The final installer is `RemoteDebugger-0.2.3-Setup.exe`, SHA-256 `F667C72CEF4697A27327A9CC8D1D6774CBF6C83301837F8C099501FA9A48A40A`. Request `executable-test-20260912T115939265Z-b997850e` passed: the installer exited successfully, ran without elevation, installed beneath the guest user's LocalAppData, created the per-user Start menu shortcut and HKCU uninstall registration, and required no Windows restart. Broker and guest harnesses succeeded, application assertions passed, process cleanup found no survivors, worker 1 ended Off, adapters were disconnected, no evidence warnings occurred, and the disposable payload child was reported deleted and independently absent on disk. This replaces the earlier passing installer candidates.

## 0.2.2 — discard stale pending frames

September 12, 2026: **127/127 unit tests passed** (96 Core and 31 platform). Five new regression tests verify replacement of 1,000 pending frames by the newest frame, selection only after a blocked UI context resumes, cancellation without replay, transport-error propagation for reconnect, and receiver shutdown when presentation fails.

The signed Release executable has SHA-256 `D03C003C402F2B9D58818DCEE60EE5161A89C170E55581FB232E6A98C746E14E`. Its receiver now acknowledges receipt independently of presentation and retains one pending frame with replacement of older frames. The sender still captures only after the previous receipt, so there is no sender-side capture queue. A JPEG already in transit is not interrupted midway; the five-fps cap and image encoding are unchanged.

The Release CLI provides a bounded `--present-delay-ms` diagnostic using the same stream-delivery implementation as the GUI. Presented sequence numbers and skipped-frame counts provide direct evidence that a slow consumer receives the newest pending frame instead of draining historical images. This does not claim that every source of network or rendering latency is eliminated.

The live evidence for request `executable-test-20260912T093614110Z-db329403` recorded a six-second, five-fps stream with a 1200 ms presenter delay. It presented sequence numbers **0, 6, 12, 18, 23**, skipping **19 intermediate frames**. Mean capture/encode time was 39.8 ms. This used the exact Release executable for agent and CLI inside one disconnected guest; it is a slow-consumer test rather than a physical-network latency measurement.

The terminal result for that request **passed all required assertions, with 29 passing checks and no failures**. Existing pairing, live viewing, input preference, tray restoration, termination and second-session recovery also passed. The actual Release live-view screenshot was visually inspected. The three optional blocked checks remain LAN/provisioning and privileged OS power-request acquisition/release. Broker and guest harnesses succeeded, worker 1 ended Off before recycling, process cleanup verified no survivors, all adapters were disconnected, and no evidence warnings occurred. Payload and read-only Release-input children were reported deleted and independently absent on disk; input cleanup succeeded. As in the prior run, independent host disk-attachment enumeration is unavailable to the development token, so that part relies on the broker cleanup result. Physical-PC latency and multiple-DPI behavior were not revalidated.

Installer request `executable-test-20260912T093643402Z-c65d2766` passed for `RemoteDebugger-0.2.2-Setup.exe`, SHA-256 `E7A7598F31C5FC13E7BAB58E1C3FD2509979B4DABE94DFB04F819FA481BF23E9`. The log confirms non-administrative installation beneath LocalAppData, the Start menu shortcut, HKCU uninstall registration, and successful completion. Broker and guest harnesses succeeded; application assertions passed; worker 2 ended Off before recycling; process cleanup had no survivors; the payload child was deleted and independently absent on disk; adapters were disconnected; no evidence warnings were reported.

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
