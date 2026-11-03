# Runtime retirement and unified update checking

Implemented in 0.2.0, stage 4 of the
[lifecycle contract](release-0.2.0-lifecycle-plan.md). Installer maintenance and
release preparation are documented separately. Per-user installation and
the current voice-cloning model source are unchanged.

## One check action, separate installation actions

Settings has one **Check for updates** button in its page header, with an activity
indicator and a narrow-window stacked layout. It first refreshes the runtime
release comparison, then invokes the existing local model verification and
anonymous model checker. A normal runtime/network failure still permits model
checking; each checker reports its own status. Duplicate manual/startup checks
are guarded, and queued automatic model checks wait for the shared manual batch.
Manual model checks still bypass their cached interval.

The shared manual batch blocks conflicting model/runtime mutations, model
selection, generation, export and folder picking. Background checks retain their
existing scheduling and lighter concurrency rules. Closing cancels through the
existing setup token; checking never downloads or installs assets.

Runtime **Update** is separate and visible only for an available compatible
release, or while installing it. Model rows retain their individual **Update**
actions. There is no Update all action. The runtime confirmation explains that
inactive downloaded versions will be removed after successful activation and
busy versions retried at startup.

## Activation and retirement boundary

Existing download/hash, ZIP/PE/platform and local voice-export contract validation
still precede atomic runtime identity activation. If activation verification
fails, the existing previous-identity restoration remains. Failed preparation
does not trigger post-update retirement. The small identity backup is recovery
metadata, not a selectable retained runtime version.

After successful activation, `CleanupInactiveAsync` runs under the existing
`runtime-update.lock`. It enumerates only canonical downloaded-version directories
with installation receipts, excludes the selected runtime, and verifies the
active executable/identity before retiring anything. It rechecks active identity
for each candidate. Startup uses the same lock and cleanup; generation/export
never scan old versions. With no inactive candidate, the extra cleanup pass does
not rehash the active executable.

Each candidate must contain exactly the supported executable, license and receipt.
Receipt release/asset/path/hash checks establish ownership. All three files must
be regular independent files and exclusively available, with executable/license
hashes and receipt content checked again through their held handles. Busy,
changed, malformed, linked or unrecorded content stays preserved. An invalid
candidate does not prevent cleanup of other independently verifiable versions.

The active runtime and application-bundled baseline are never retirement targets.
Unknown directories without an installation receipt are not adopted. Model blobs,
settings, reference audio, profiles and shared HF cache remain outside this scope.

## Durable interruption recovery

Before moving a verified inactive directory, the existing operation mechanism
publishes a `runtime-retirement` record in runtime staging. It uses the same
schema-1 store/operation-directory binding, with an optional `Hashes` map for the
three exact `payload/` files. Older ordinary download records remain accepted;
only retirement records may carry this complete SHA-256 map.

Windows requires child handles to close before directory rename. The source
directory is then moved atomically into the recorded operation's `payload`.
The record retains the independently verified executable, license and receipt
hashes across that boundary. Cleanup checks the complete layout and hashes before
the first deletion, and rechecks each hash through its exclusive deletion handle.
Changed resources are preserved even if modification occurs between preflight
and deletion. Empty directories are removed through checked handles; the journal
is removed last. Empty release-parent pruning after retirement is best effort.

Startup accepts interrupted moves and partially deleted payloads, reverifies the
active runtime before retirement-record cleanup and retries idempotently. Unknown
fields, duplicate hash metadata, conflicting journals, bad hashes, links and
changed physical bindings retain the affected record/files. Unrecorded legacy
staging is still not swept. See [temporary recovery](temporary-download-recovery.md).

Successful activation with incomplete retirement remains a successful update
with cleanup pending. Its verified active version stays selected. The result is
shown in the existing runtime card; startup recovery uses the existing storage
cleanup result card. There is no user-facing rollback after successful activation,
and no archive of old downloaded versions is intentionally retained.

## Targeted validation

```powershell
dotnet run --project tests/PocketTTSStudioLite.RuntimeCleanupChecks -c Release
dotnet run --project tests/PocketTTSStudioLite.ModelRemovalChecks -c Release
```

The runtime checks link production identity/store, receipt verification, download,
ZIP/PE checks, retirement journals and Win32 deletion. Only the private data root,
platform/CLI diagnostics and HTTP responses are substituted; the ZIP executable
is inert x64 test-apphost bytes and is never launched. The bundled-baseline fixture
exists only in this check executable's output and is removed after verification.

The 22 runtime checks cover active/bundled preservation, idempotence, locks,
changed executables/licenses/receipts, unknown files/directories, independent
cleanup, invalid active identity, interrupted retirement, partial deletion,
post-move changes, exclusive-handle hash refusal, hardlinks/junctions, cancellation,
successful installation, busy postactivation cleanup/retry and corrupt-download
failure before activation. All 61 existing model/update/temporary-recovery checks
also pass, with one separate symlink test skipped for missing Windows privilege.

Release x64 build checks the WinUI bindings/handlers. Actual GUI layout, a live
combined check with network failures, real upstream runtime downloads, live CLI
compatibility, cloning/export after update and power-loss testing remain manual
validation. No application launch, real user-data cleanup, installer execution,
new test framework or dependency is part of these checks.
