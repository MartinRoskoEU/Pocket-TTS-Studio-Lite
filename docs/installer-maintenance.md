# Per-user installer maintenance and checked uninstall

Implemented in 0.2.0, stage 5 of the
[lifecycle contract](release-0.2.0-lifecycle-plan.md). Version and final package verification are recorded in the
[0.2.0 release record](releases/0.2.0.md). This source change does not update any
installed copy of the application or its uninstaller.

## Scope and authority

Keep `PrivilegesRequired=lowest`, the existing AppId, user shortcuts and current-user
uninstall entry. There is no ProgramData store, HKLM registration, certificate or
elevated Out Loud worker. The existing optional Media Feature Pack prerequisite
is still the only system operation requesting administrator approval.
Destination selection requires a dedicated local-drive directory; drive roots,
UNC/network locations and linked destinations are rejected by the checked lifecycle.

Inno owns confirmation, prerequisites, file publication, shortcuts, its bootstrap
DLL and Windows uninstall metadata. A self-contained .NET console helper owns
installation receipts and private-data plans. The existing native installer DLL
adds handle-based payload deletion; it does not synthesize speech or register SAPI.
The worker uses the same version source, services and .NET notices as the app.
An installer instance lock remains held across payload publication/removal; its
create-new, delete-on-close file is never adopted from an existing arbitrary file.

Packaging publishes the worker under `maintenance/worker`, generates a SHA-256
package file inventory and embeds its hash, worker file hashes and native-bootstrap
hash in the Inno script. Setup materializes its reviewed worker in Inno's temporary
directory; uninstall verifies and pins its installed worker inputs before launch.
The setup CPU import can already have extracted/loaded the native input DLL;
preparation reuses that file rather than writing over a mapped image, and retains
hash verification before making its separate DLL copy. See Inno's
[automatic DLL extraction](https://jrsoftware.org/ishelp/topic_scriptdll.htm).
No password, OAuth, network, model download or shared HF cache access is involved.

## Reinstall and upgrade

Before copying, the worker verifies dedicated, separate application/data roots,
rejects existing linked destinations and checks any previous installation receipt
against its physical root identity and exact file hashes/identities. It refuses
active instances using this application payload. A flushed `upgrade-plan.json`
retains the preceding inventory before publication. Retrying the same prepared
package can finish an interrupted file-copy/verification phase.

After Inno publishes the new payload, the worker verifies all package files and
the package manifest itself, atomically publishes `installation.json`, and retires
only preceding files absent from the new package. Changed or busy obsolete files
remain listed in `PendingObsolete`; setup reports incomplete maintenance and a
nonzero exit code, with the application data untouched. A later same-package
maintenance attempt reuses the ownership records. No old uninstaller is invoked.

Models, runtime updates/selection, preferences, export destinations and external
profiles are not modified by application upgrades. Unknown extra payload files
are preserved. A previous inventoried file modified by another writer stops the
affected maintenance instead of being silently adopted.

An old 0.1.0 installation has no authenticated payload inventory. This change has
no independently verified baseline of every historical 0.1.0 payload file, so it
does not sweep that directory. Setup replaces only the new package's declared
destinations, records the newly verified payload, and logs that uninventoried
legacy extras were preserved. Such files remain outside future deletion plans.
This is deliberate; complete removal of legacy extras is not claimed.

## Uninstall lifecycle

The existing interactive choice remains: keep private data (the default), or
remove verified application-managed private data. Silent uninstall keeps private
data. A completed earlier private-data choice may be resumed without repeating
deletion. Confirmation occurs before the `usUninstall` work; cancellation before
that point does not run private-data removal. See the official
[Inno event lifecycle](https://jrsoftware.org/ishelp/topic_scriptevents.htm).

For private-data removal the worker first requires matching HKCU Registry64
installation location, no running Pocket TTS Studio lite instances, exclusive
model/runtime installation locks, the existing model writer guard and native-job
quiescence. It captures a bounded immutable `uninstall-plan.json` under the
application's maintenance directory before deleting any private file. The plan
binds both physical roots, relative paths, exact content hashes and file identities.

Deletion authority comes from canonical schema-2 model manifests/pending jobs,
verified downloaded-runtime receipts, completely validated temporary-operation
records, supported metadata schemas and empty owned operation locks. The shared
production blob, receipt, path and Win32 file guards are reused. Unknown blobs,
unrecorded staging, unsupported settings content and user files stay unplanned.
No selected export-folder value is followed. Empty recorded operation directories
are captured too; containing directories are removed only when empty and with
their expected physical identity.

The complete remaining private plan is reverified before each attempt. Deletion
rechecks content/identity through exclusive handles. Interrupted deletion accepts
already missing planned files; a replaced root or changed resource is refused.
An invalid authoritative model inventory or changed known runtime/blob stops
before private-plan deletion. Optional unknown metadata and invalid temporary
operations do not grant cleanup authority and stay preserved.

After private cleanup (or explicit retention), the worker flushes an
`uninstall-state.ini` completion record. Native code then preflights all current
compiled payload paths, pins ancestors, verifies independent regular files and
SHA-256, and retains exclusive DELETE handles for the whole batch before deleting
the first file. Missing planned files are accepted on retry. Links, hardlinks,
changed files and active users stop safely. Unknown files keep their directories.

If native deletion is interrupted, Inno exits nonzero before removing its
uninstall entry/bootstrap. The persisted completion record permits retry even
after worker files have been removed. The native bootstrap is removed by Inno
only after checked payload removal succeeds. Generated metadata is removed only
using the hashes recorded by the worker, and the completion record is queued last
in the checked native batch. It is retained if preflight fails. After successful
native removal, the instance lock and directory pins are released before Inno
removes its bootstrap and the registered empty payload directories (deepest first,
including empty directories shipped in the package). Unknown content keeps its
parents. These `dirifempty` entries are unconditional: Inno evaluates their `Check`
during installation, so uninstall-only state cannot be used to register them.
See the official [recording implementation](https://github.com/jrsoftware/issrc/blob/main/Projects/Src/Setup.Install.pas)
and [empty-directory cleanup](https://jrsoftware.org/ishelp/topic_uninstalldeletesection.htm).
Failed native removal exits before Inno starts its removal pass.
The app refuses startup while uninstall plans/state exist, preventing
it from recreating data during an incomplete installed uninstall.

`UninstallLogMode=overwrite` replaces older file-removal log history on the next
installation. New payload files use `uninsneveruninstall`; their deletion belongs
to the checked native batch. This prevents Inno's older append history from later
removing files that the new ownership checks preserved. Native current-user
shortcut choices and the stable uninstall identity remain unchanged.

## Failures, diagnostics and limits

Automatic setup/uninstall logging records worker action, result and report path.
The UI distinguishes installed-but-unverified maintenance, refusal before payload
deletion and interrupted deletion. Uninstall shows activity while checking/removing
owned resources. No process is killed automatically. Retain recovery records and
rerun the matching installer/uninstaller after resolving busy resources.

Unknown files, unregistered assets, external profiles, shared caches, shared
Microsoft components and the Media Feature Pack are preserved. The private root
can remain when it contains unknown content or unrecorded directories. Failed
or torn metadata publications (`*.tmp`) are preserved for review instead of being
silently overwritten.
Unrecorded reference-audio fallback sessions also remain preserved; filenames alone
do not establish ownership of those older temporary files. Cancelled preparation can retain a bound upgrade plan in
the selected directory; resuming the same package is supported. These records
are not rollback archives. No promise of a downgrade to 0.1.0 is added.

## Targeted verification

```powershell
.\installer\check-maintenance.ps1
```

This compiles the native DLL with `/W4 /WX`, runs 23 isolated managed/native
maintenance scenarios, builds the worker and compiles the Inno script with
`--output=no` against inert compiler fixtures. Evidence is retained under a fresh
`artifacts/installer-checks/<guid>`; no product installer is produced or executed.
The checks cover fresh/legacy ownership, reinstall, partial publication/resume,
obsolete files/locks, keeping/removing private data, unknown metadata/files,
shared model blobs, changed data, external exports, staged downloads including
empty operations, root replacement, native preflight, missing-file retry,
hardlinks/junctions and linked setup destinations.

The 61 model/temporary checks and 22 runtime checks pass after shared guard changes
(one separate symlink check is skipped for missing Windows privilege). Application
and worker Release builds pass without warnings/errors. The published self-contained
worker's prepare/commit route was also exercised on inert fixture payload only.
No live product data, installed registration, certificate, real installer, Sandbox,
authentication, model download or speech operation is part of this verification.

The complete 0.2.0 package has been built and source-payload verified; see the
[release record](releases/0.2.0.md). The user confirmed working installation and
corrected uninstall behavior following the Sandbox test report. Separate upgrade,
silent-retention, interruption/retry and unknown-file preservation scenarios were
not individually reconfirmed in the final review. Source/fixture verification and
user-reported live results are recorded separately.
