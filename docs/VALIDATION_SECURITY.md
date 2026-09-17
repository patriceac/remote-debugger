# Security setup and migration acceptance (0.4.0)

The signed Release SHA-256 is
`A655CFFEA3F177665FAC861F17B46451F9FC76C83E774A4A720B34FED87F13B3`.
The local publisher is explicitly enrolled, not publicly CA-trusted.

## Focused automated checks

- 158 Core and 62 Platform unit tests passed, including envelope randomness,
  wrong-passphrase/tampering rejection, bounded parsing, protected import,
  remembered authorization, safe reuse of an existing session and the pairing reset.
- Six relay tests and TypeScript checking passed. Relay tests exercise scoped
  discovery and reject legacy access to protected registrations/channels,
  including a forged scope header.
- Hyper-V request `executable-test-20260917T220913130Z-90e8d0fa` passed all seven
  declared security acceptance checks using the signed Release GUI and CLI.

The native test entered a test passphrase into masked GUI fields, created the
encrypted setup, reused an already paired controller session, and migrated a disposable receiving process over the production
relay, rejected its previous discovery/session credentials, and reconnected to
obtain its desktop. It also recovered an unacknowledged commit after the receiving
process restarted with a new route, repeated completed migration, and verified
wrong-password rejection plus remembered access on a fresh configuration.
The request was restricted to the disposable guest's observed routing ID;
other production computers were never migration targets in this test.

The setup and migrated-state screenshots were visually reviewed. The controls
and text fit, and the table distinguished the protected guest from an offline
pending fixture. Earlier diagnostic runs exposed a hidden create button and
test-driver modal focus issues; these were corrected before the passing run.

Broker and guest harnesses succeeded, application evaluation passed, the VM
ended Off, process cleanup found no survivors, and there were no terminal
evidence warnings. Both read-only auxiliary inputs used VHDX transport. The
broker attested the requested InternetOnly policy and guest configuration,
disconnected the request adapter, removed its lease and reported all adapters
disconnected. All three disposable child files were reported deleted and their
absence on disk was verified. Independent Hyper-V attachment enumeration was
unavailable to the non-administrator host account; no elevated workaround was
used. This run is not a new exhaustive network-isolation penetration test.

## Installer

Request `executable-test-20260917T220115553Z-48c61293` passed the disconnected
installation check for the generic signed installer, SHA-256
`BA452D4C0FD056B241E1FB661B0F27E328873CB42CEDF702663856B5638D09CE`.
Its process exited successfully and the log confirmed per-user installation,
no administrator privileges, Start menu/startup shortcuts and HKCU registration.
Both harnesses succeeded, the VM ended Off, process cleanup passed and the
payload child was deleted and absent on disk, with no evidence warnings.
A prior attempt failed before application launch because its pool worker was
not running; it supplies no product acceptance evidence.

The final encrypted private installer, SHA-256
`5AAC5D3BF02BBA303BBDC47C7201A88BDD3E82CF00EBF9A9ECFF3608445F9563`,
passed all four checks in disconnected request
`executable-test-20260917T221113963Z-8431a3f2`. Installation staged only an
encrypted envelope, left the Windows account unauthorized, and first normal
launch displayed the enabled masked passphrase prompt. The screenshot was
visually reviewed. Both harnesses and application evaluation passed; the VM
ended Off, process cleanup passed, payload/input children were deleted and
there were no evidence warnings. The installed Release had the final hash above.

## Boundaries

The security test exercised synchronization of matching binaries followed by
the credential transition. It did not repeat the older provisioned updater
replacement matrix, first-time administrator consent, or multiple-DPI review.
Fresh unlock/reinstall behavior was evaluated through the platform code; the
actual private installer additionally proved installation and first-launch
passphrase gating without entering or exposing the owner's passphrase.
No application acceptance tests ran on the physical host. Host installation and
authorized migration of real computers are deployment operations.

## Authorized deployment

The owner's existing Windows 10 computer was updated remotely from 0.3.0 to
0.4.0 using its saved authenticated session and protected updater. The controller
and running receiver reported the same final signed executable hash. Security
migration completed, and a new authenticated connection verified the protected
profile. After the owner confirmed the device inventory was complete, the legacy
relay secret was removed. Legacy discovery returned HTTP 401; protected discovery
returned HTTP 200 and a fresh remote status call still succeeded. The temporary
verification grant was released, leaving support available for normal pairing.
