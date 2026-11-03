# 0.2.0 model, runtime and installer lifecycle contract

This contract defines the implemented ownership, update and recovery rules in
**0.2.0**. `Directory.Build.props` is the single version source. Current validation
and user-confirmed installation, uninstall and scroll behavior are recorded in the
[0.2.0 verification record](releases/0.2.0.md).

The implementation consists of [temporary download recovery](temporary-download-recovery.md),
[model updates](model-updates.md), [runtime cleanup/unified checking](runtime-cleanup.md),
and [per-user installer maintenance](installer-maintenance.md).

## Scope and invariants

- Keep the current supported voice-cloning models and the official
  `kyutai/pocket-tts` repository. Do not substitute Out Loud's
  `kyutai/pocket-tts-without-voice-cloning` models or copy its synthesis-only
  validation. Preserve trusted native configuration and voice-cloning checks.
- Keep per-user installation, `PrivilegesRequired=lowest`, current-user shortcuts,
  and stable Inno AppId `{336B2514-DBF3-4566-9302-95EE5D2D7DD3}`. The default program
  directory remains `%LOCALAPPDATA%\Programs\Pocket TTS Studio lite`.
- Keep private managed data at `%LOCALAPPDATA%\PocketTTSStudioLite`, separate from
  installed program files. Do not adopt Out Loud's ProgramData, SAPI, certificate,
  global registration or elevated-worker infrastructure. Preserve the existing
  optional Windows Media Feature Pack prerequisite handling.
- Preserve native `.safetensors` export, local audio processing, and existing
  preview/export workflows. Updates must not upload audio or profiles.
- Reuse existing inventory, identity, validation, operation leases and locks.
  Do not create a parallel list of installed models or a fallback model selector.
- Preserve existing Hugging Face OAuth for gated downloads. Checks remain
  anonymous; tokens stay session-only and never enter persistent operation records
  or logs. No automatic application updater is introduced.

## Ownership and deletion authority

Location alone is not proof of ownership. The implementation must distinguish
these resource classes before planning deletion:

| Resource | Ownership proof and permitted cleanup |
| --- | --- |
| Installed application payload | A verified installation file inventory. Maintenance may retire obsolete inventoried files; uninstall may remove owned payload. Inno continues to own its uninstall metadata and shortcuts. |
| Private model blobs | Canonical content-addressed references from valid model manifests or durable pending-cleanup records, with file identity/content checks. Delete only when no remaining manifest references the blob. |
| Downloaded runtime | Canonical release/hash location plus verified installation receipt, executable and license hashes. Delete only verified inactive downloaded versions. |
| Bundled runtime | Part of the application payload. Model/runtime update cleanup must never delete it; application maintenance/uninstall handles it as payload. |
| Temporary download files | A durable operation record published before payload creation, describing an exact operation and bounded relative file layout. Cleanup is confined to that operation. |
| Settings, inventories, backups and lock files | Explicit supported metadata names/formats, verified paths and exclusive access. A filename by itself is insufficient. Backups are recovery metadata, not selectable rollback versions. |
| Unknown, changed or unsupported content | Preserve it and report the applicable limitation. Do not add it to a deletion plan just because it is inside a managed root. |
| Reference audio, external exports, shared Hugging Face cache and shared Microsoft runtimes | Outside managed-data cleanup authority. Preserve them. |

Directory links, file links, hardlinks and redirected roots must not authorize
deletion of external targets. Pin and revalidate the relevant physical paths and
file identities. Existing valid model manifests, including incompatible or
unverified installations, still contribute references protecting shared blobs.
Unregistered blobs do not become installed models or automatic cleanup candidates.

Unknown extra files must not prevent removal of independently verified owned
resources. Leave their containing directories when necessary. Invalid ownership,
corrupt required metadata, changed planned resources and files in use may block
the affected operation; they must not be silently bypassed or reported as success.

## Checks and explicit update actions

One Settings **Check for updates** action will invoke the existing runtime and
model checkers. Failure of one checker must still allow the other to report its
result. Preserve identity-bound model result caching and manual interval bypass.
Startup may check availability and retry recorded cleanup, but never install an
update automatically.

Keep separate **Update** actions for the runtime and each eligible model. This
release does not add an Update all action. Confirmation identifies the installed
and proposed release/revision and explains removal of superseded files. Cached
availability is not installation proof; recheck the confirmed identity before
commit and reject stale confirmations.

## Model update transaction

1. Verify the registered old model, active runtime and supported trusted
   configuration. Require the same confirmed installation identity.
2. Resolve the confirmed immutable official repository revision and complete
   file hashes/sizes. Preserve the current trusted configuration structure and
   supported model set; do not execute arbitrary remote configuration.
3. Publish operation ownership metadata before creating temporary payloads.
   Reuse verified private blobs, download missing assets, and verify the complete
   candidate including voice-cloning capability before publication.
4. Under the existing publication/inventory guards, recheck runtime and model
   identity. Atomically replace the model record and record superseded blob
   references for cleanup in the same durable inventory commit.
5. Refresh the selected model object through the authoritative setup service.
   Preserve the selected alias when usable; otherwise show it unavailable and
   block dependent work. Never silently select a different model.
6. Recompute references from current manifests and remove only verified
   unreferenced superseded assets. Invalidate stale update results. Keep an
   unfinished cleanup record until cleanup and cache invalidation are complete.

Before commit, cancellation/failure preserves the old registration and assets.
After commit, cleanup failure does not undo a successful update. Report success
with cleanup pending and retry safely. No user-facing rollback is provided after
a successful update. Shared dependencies remain while any model needs them.

## Runtime update transaction

Verify the active runtime and confirmed official release/asset, then download,
verify and validate the candidate using the existing identity and CLI contract.
Preserve required model aliases and the native voice-export contract. Only a
fully verified candidate may become active through atomic identity publication.

Keep the existing protective restoration of the previous identity if activation
verification fails. This recovery boundary ends before old versions are deleted.
After successful activation, remove verified inactive downloaded runtime versions
under `runtime-update.lock`; keep the active runtime and bundled baseline.
There is no user-facing rollback or retained archive of old update versions.

Busy or unverifiable versions remain untouched. Report pending cleanup and retry
at startup under the same guard, never from generation/export. Reverify the active
identity before cleanup so it cannot delete the currently selected version.

## Interruption and concurrency

Durable operation records must contain only bounded operation IDs, relative
allowlisted resources and necessary ownership/integrity information; no arbitrary
absolute deletion destinations, credentials, audio contents or export paths.
Validate the complete recorded operation before deletion and keep its record
until the deletion is verified complete. Retry must be idempotent after a crash
between deleting a file and acknowledging it.

Coordinate model installation/update/removal, runtime update, recovery and
installer maintenance with existing model/native operation leases and process/job
cleanup. Do not delete loaded assets or forcibly terminate unrelated processes.
Stop owned idle runtime sessions through the existing lifecycle where required.
Specify and test lock ordering during implementation, including avoiding nested
reacquisition of the installation lock during post-update model cleanup.
Window close must await safe cancellation/commit boundaries or retain recovery
records. Once commit starts, finish activation/recovery without cancellation.

## Application maintenance and uninstall

Same-version reinstall and application upgrade preserve managed model/runtime
data, selected alias and settings. They update the application payload, remove
only proven obsolete payload files, and retire completed owned maintenance
working files after verification. Interrupted maintenance must retain sufficient
recovery state; do not run the old uninstaller as an upgrade step.

Preserve the existing interactive uninstall choice to retain or delete private
data. Silent uninstall retains private data. If deletion is chosen, build and
verify an owned-resource plan before removing the application components needed
to execute it. Replace whole-private-root deletion with checked resource cleanup.
Reference audio and profiles in user-selected external locations remain untouched.
Unknown content is preserved, with nonempty parent directories left in place.

Distinguish refusal before deletion from partial cleanup. Persist resumable work
and actionable diagnostics when deletion is incomplete; retain the trusted cleanup
mechanism required to retry it. Do not report complete removal merely because
Inno removed application files. No live installation or uninstall is authorized
by source/build verification.

## Upgrade compatibility: 0.1.0 to 0.2.0

- Accept valid existing private schema-2 model inventories and verified assets
  without redownload or conversion. Preserve their pinned revisions and native
  configurations until the user explicitly updates a model.
- Preserve a valid runtime identity and downloaded-runtime receipts. An application
  upgrade must not reset the selected runtime to its bundled baseline.
- Preserve selection, export-folder preference, update settings, and valid pending
  model-removal records. Never replace corrupt metadata with an empty inventory.
- New update/operation metadata must be versioned and validated. Use additive
  optional fields when safe; test acceptance of actual 0.1.0 record shapes.
  Compatibility with downgrading to 0.1.0 after new updates is not promised.
- Unsupported development inventories and external/shared-cache installations
  remain unsupported; no automatic adoption, path rewrite or migration is added.
- Upgrade must work from an installation lacking the new payload inventory.
  Define and test a trusted 0.1.0 payload baseline/bootstrap before deleting legacy
  files. Never invent ownership from a directory scan or hash the current files
  and treat that alone as proof. If old ownership cannot be established, preserve
  those files and report the limitation; install the new verified payload without
  claiming that all obsolete legacy files were removed.
- Unrecorded old staging is not automatically deletable. Any proposed recovery
  must independently establish ownership; otherwise preserve it. No broad sweep
  of private directories, shared caches or ignored repository outputs is allowed.

## Verification

Use isolated fixtures for update transactions, shared-file ownership, locked files,
interrupted cleanup, unknown content and external-link refusal. Actual offline
cloning/export, upgrades and uninstall behavior require product testing; a build
alone does not establish them. Current results and their limits are recorded in the
[0.2.0 verification record](releases/0.2.0.md).

Current behavior references: [private model storage](private-model-storage.md),
[model removal](model-removal.md), [model update checks](model-update-checks.md),
[runtime updates](runtime-update-checks.md), and [Windows installer](windows-installer.md).
