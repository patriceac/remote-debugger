# Controller, support permission and installer corrections

2026-09-25. Implementation authorized by Patrice after the 0.5.21 audit. **Verification in progress; this is not a release acceptance declaration.** The original 37-case audit remains in [ROLE_SUPPORT_AUDIT_RESULTS.md](ROLE_SUPPORT_AUDIT_RESULTS.md).

## Implemented behavior

- Outgoing discovery, pairing, CLI/API/MCP calls and saved sessions require a valid enrolled controller key. Receivers require a TLS client certificate proving possession of that same controller authority. Pairing codes, private-network membership and Windows elevation cannot substitute for it.
- Removing controller authority also denies requests from a running cached CLI worker or MCP process. Local authorization failures return `access_denied` so callers do not misclassify them as transport failures.
- One **Support enabled** preference covers every incoming support operation. Agents keep it ON and disabled; Take control is visible and disabled. Controllers default OFF and may opt in. Turning OFF revokes grants, persistent channels, update/restart tickets, running operations and relay availability while leaving outgoing authority intact.
- New controller consent is stored in `support-enabled.json`. Missing, invalid and legacy maintenance settings do not enable incoming support. The controller password remains required for new enrollment.
- LAN directory replies require a signed controller challenge. Relay listings and connection opening require a short-lived signed request with durable nonce replay rejection, in addition to network membership.
- Setup requests elevation before protected writes. Original-user work now runs from the installed Program Files executable; helper provisioning uses an identity-checked local pipe rather than a file in elevated temporary storage. Setup checks installed versions before shutdown or overwrite. Terminal synchronization errors stop showing preparation/reconnection progress.

## Compatibility and limits

The final production release is 0.5.23; the intermediate 0.5.22 package was already copied to Drive, so a new version avoids conflicting with the strict remote replacement policy. Upgrade the controller first. The new controller can contact an older agent to update it; older controllers cannot authenticate to a hardened receiver or the hardened relay. Every endpoint needs the new version to enforce the new incoming boundary on direct connections.

The new installer refuses a newer installed version. Previously distributed installers do not acquire this guard retroactively, and an administrator can independently replace application files. The application updater continues to accept only strictly newer releases; equal-version different bytes are rejected and identical bytes require no replacement.

LAN discovery proves requester authority and correlates replies. It does not establish a separate signed responder directory identity; endpoint TLS pinning and pairing remain responsible for target authentication. No real family PCs or production support sessions are used in qualification.

## Artifact identities

| Artifact | SHA-256 |
|---|---|
| Production 0.5.22 Release | `990A280581BDD888C6CEF016E28487D337D6E4E74FAAC3C684048C2EB93F4D15` |
| Production private setup | `CB36F3026B74E0D196428465ABB5BA4C5FAC4ED5A5487DF79037CCE8DE7A7E51` |
| Same-source 0.5.22 disposable-authority fixture | `2BB2853358F260D871C7B82FB929BD3D5DB5696A1DC5E5EA4AA1227AF04CF30A` |
| Synthetic future 0.5.23 fixture | `BD41BB22FE6BEA6D7888CEAEFA78E52350203D1DA15716BC96F04B2379D3830B` |
| Distinct same-version 0.5.22 fixture | `B1AF6782F92FC8CEE3F30E144593441F779069E34880499E4DBAB48CAEC82BB4` |

Diagnostic fixtures use the repository's public test authority and must never be distributed or installed on real PCs. Production setup was copied to `C:\Users\patri\My Drive\Dev` with its hash verified; the previous production installer there was removed. No test fixture is published there.

## Completed verification

Latest Luna Max agents execute tests; the parent writes all product/test code. Latest normal-production unit checks: **225 core**, **61 focused platform**, and **21 connected CLI/worker tests** passed. Earlier focused platform coverage passed 67 checks. Relay unit tests: **11 passed**; TypeScript check and Worker dry-run build pass.

| Scenario | Evidence and result |
|---|---|
| I01 normal setup / accepted UAC | `executable-test-20260925T135953972Z-76e74417`: full harness, UAC contract, Before/After and product assertions pass; setup exits 0. |
| I03 standard user / different administrator | `executable-test-20260925T135953972Z-5d64488a`: full pass. Separate account SIDs; installed helper registered to the initiating user; startup shortcut owned by that user; LocalSystem service running; no controller key or unlocked network credentials. |
| I04 declined UAC | `executable-test-20260925T135604961Z-8067fce5`: full pass; exit 2 and no installation side effects. |
| Six role rows, disabled agent controls, default controller OFF, certificate-less backend rejection | `executable-test-20260925T135731993Z-2a1a3d88`: all corresponding assertions pass in the signed fixture, with authorized ordinary and administrator operations. |
| Live incoming revocation | Same request: screen, input, heartbeat and update channels close after the actual OFF toggle. Saved grants fail while OFF, after ON, and after process restart. A new pairing works after ON; OFF survives restart. |
| Source-OFF outgoing GUI | `executable-test-20260925T143447641Z-790956c3`: 6/6 pass with signed fixture `2AB4E550E662C5B40AFC34AA6A65B6C931027C93E02321A18A5BF6F43E3F5570`. H.264 live screen at 6.9 fps, source OFF preserved. Manual input and terminal errors now survive discovery refresh. |
| Private relay matrix | `executable-test-20260925T142119588Z-a7cb937f`: overall pass, 71 assertions plus one explicitly labelled directory observation. Membership-only directory/connect rejected, authorized private-ID pairing and WSS-only system/process/files/screenshot/command/admin operations pass. Packaged local Worker in an isolated guest, not the deployed service. |
| Two-VM LAN matrix | Receiver `executable-test-20260925T145858338Z-0793e5d3` (15 checks) and sender `executable-test-20260925T145858453Z-3f2a0e00` (38 assertions plus one directory observation): all pass. Six role combinations, controller-only discovery, authorized system/command/admin/text input/connected API, and update metadata policy. IsolatedTestNet, no host/LAN/Internet route. Firewall prompt handling, network boundary and cleanup all verified. |
| E06 / I02 / I05 / U06 / U07 reinstall and local downgrade | `executable-test-20260925T144901902Z-98e507db`: 18/18 pass. Fresh agent and agent reinstall remain agents with Ready helper; controller reinstall preserves OFF; forward .22-to-.23 installation preserves OFF; older .22 installer exits 1 without replacing .23 bytes. Explicitly elevated signed disposable-authority fixture. |
| Password enrollment, consent recovery, End support and live CLI/MCP revocation | `executable-test-20260925T152149830Z-3b34bf74`: 48/48 pass, including wrong/cancelled/correct controller password, missing/corrupt/legacy consent, agent session termination and cached CLI/MCP denial after authority removal. Fixture executable SHA-256 `744D7E7D476B2C32D4B59BC6D5F31552261AAA05117DAB769A526BFFF0AE29FA`. |

The initial I01/I03 verifier expected helper version 0.5.21.0 and rejected a correctly installed 0.5.22.0. After fixing that diagnostic predicate, both accepted-install cases passed on the unchanged setup bytes. `firewallReady=false` in disconnected installer runs is an observation, not a failed helper assertion or proof of LAN firewall readiness.

The first role request has **69/70** assertions passing and a successful harness/cleanup contract; its outgoing GUI failure is superseded by the later scoped pass. The intermediate GUI retry `...141430218Z-0bb7e22b` also failed to establish support; its automatic discovery refresh erased the displayed error. The final GUI run uses the subsequent UI correction and serialized VM scheduling; this does not establish the cause of every earlier transport failure.

Initial two-VM LAN receiver `...140603237Z-c952ea33` passed 15 checks; sender `...140603237Z-ba7a27e0` failed controller-to-agent pairing and input focus. A Windows Firewall prompt covered the receiving desktop. The sender's internal audit correctly failed even though the original wrapper asserted only file presence; later requests assert `/passed == true` explicitly. The next receiver `...144149980Z-635b97b4` failed the harness's expected firewall-prompt contract before application evaluation. None of these is full LAN acceptance.

Reinstall request `...142143116Z-e6acaebf` passed fresh-agent and agent-reinstall checks, then its diagnostic omitted the directory needed for disposable controller enrollment. Its elevated wrapper also remained running at cleanup; VM shutdown and payload deletion still completed. Corrected request `...144901902Z-98e507db` passed all 18 assertions and cleanup. An unintended duplicate `...145040943Z-cdf3f594` used the wrong package root and failed before assertions; it is excluded from product results. Result files and screenshots are retained under `work/role-permission-fix` and the broker's per-request Results directories.

## In progress

Full remote update/reconnect and same-version replacement refusal; reboot persistence; terminal failure UI; exact final 0.5.23 installer acceptance. One Luna Max test coordinator runs one scenario at a time, with two workers together only for the LAN pair. The deployed relay has not yet been updated; publication is blocked on Cloudflare sign-in.

Extended request `...150727343Z-f1de72ed` stopped after three passing checks because its diagnostic package omitted BouncyCastle. The corrected package `...151243464Z-82f087e0` passed 46/48 checks: enrollment, settings recovery, session end and initial MCP status/system/upload passed; the two revocation checks found access correctly denied but mislabeled `transport_or_input`. The CLI/MCP error mapping now returns `access_denied`, with 21 focused unit checks and the final 48/48 runtime check passing. Both earlier attempts had complete isolation/evidence/cleanup; their failures remain recorded and are superseded only by the scoped final pass.
