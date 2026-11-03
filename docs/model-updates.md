# Explicit voice-cloning model updates

Implemented in 0.2.0, stage 3. The official source remains
`kyutai/pocket-tts`, with the current supported aliases and trusted embedded
voice-cloning configurations. [Runtime cleanup and unified update checking](runtime-cleanup.md)
are implemented in stage 4; installer maintenance remains a later stage of the
[lifecycle contract](release-0.2.0-lifecycle-plan.md).

## User workflow

The existing anonymous [update checks](model-update-checks.md) identify changed
required files at an immutable official main revision. A verified compatible
installed model with an identity-matching available result exposes **Update**
alongside its existing **Remove** action. Unverified/incompatible models retain
their existing management behavior and cannot be updated through this action.

Update confirmation shows the installed and proposed revisions. Cancel is the
default. The existing installation card displays update progress, cancellation
and completion/pending-cleanup results. The workflow reuses anonymous-first
downloads and the existing Hugging Face OAuth dialog when a gated file requires
authorization. The in-memory session is cleared on all outcomes; metadata queries
remain anonymous and credentials are not forwarded to CDN redirects.

Updates block conflicting model/runtime operations, generation, export and model
selection. An idle owned generation session is stopped before update work starts.
Window close uses the existing installation cancellation/await path. Progress
becomes noncancellable at activation, after which the short commit and cleanup
must finish or retain durable pending work.

## Verified preparation and commit

`PocketTtsModelInstallService.UpdateAsync` requires the confirmed inventory
identity and a full immutable revision. It holds `model-install.lock` through
preparation, commit and temporary cleanup. Before network/download work, it
rejects stale confirmations and unresolved cleanup for that alias, validates the
old configuration and verifies old assets including the voice-cloning encoder.

`PocketTtsModelUpdateService.ResolveUpdateAsync` resolves hashes/sizes from the
exact confirmed official revision, including commit-pinned tree fallbacks when
needed. Only the trusted template's existing asset paths can be updated. No
remote configuration, new alias, synthesis-only model or alternative repository
is adopted. A revision with no changed required files is rejected.

Missing bytes and the new update receipt use
[recorded private staging](temporary-download-recovery.md). Existing verified
blobs are reused without replacement. Complete candidate verification retains
size/hash checks, native offline configuration binding and required cloning
tensors. Names, dtypes and dimensions of all non-metadata weight tensors must
also match the previous model. Changed architecture requires a compatible
application release; it must not be activated using an unsuitable old config.

After verification, files are published without replacing existing blobs. Under
the exclusive model operation lease, runtime identity is rechecked and
`CommitUpdateAsync` checks the current model identity again. One atomic inventory
commit replaces the alias's manifest and appends superseded blob/receipt
references to pending cleanup. Before commit, failure/cancellation preserves the
old manifest and assets. Published unregistered verified blobs may remain reusable
after failure; they are not installed models or automatic garbage-collection
candidates.

## Receipts, compatibility and selection

Schema 2 has an additive optional `UpdateReceiptSha256` field, omitted when null.
Valid 0.1.0 manifest serialization/identity stays unchanged. Updated models store
an immutable hash-addressed JSON receipt binding alias, official repository,
revision and required file identities. Reconstruction verifies the receipt's hash,
format and correspondence to the embedded template and retained native config.
Missing/tampered receipts and changed configs cannot unlock export or further
updates. Native `.safetensors` model/profile bytes receive no proprietary changes.

The saved model selection is never rewritten by the update transaction. After
success, the existing setup refresh replaces checked model objects and retains
the active alias if usable. It does not navigate or activate another model.
An unusable selection is cleared in memory and dependent actions remain blocked;
there is no fallback selection. Existing reference audio and external profiles
are unaffected.

## Superseded files and interruption

Postcommit cleanup reuses `PocketTtsModelRemovalService` while retaining the
already-held installation lock, without reacquiring that lock. It takes the
existing model writer/native-quiescence guards, recomputes references across all
remaining manifests, deletes only unreferenced superseded blobs/receipts, and
invalidates the alias's stale update cache. Shared tokenizer/weight blobs stay
while another model references them. Old receipts contribute references just
like other model assets.

After activation there is no user-facing rollback. Locked files or failed cache
invalidation leave the existing durable cleanup job pending; a successful update
is reported with pending cleanup rather than as activation failure. Startup
retries using current references, so newly reused files remain protected. A
second update for the alias is blocked until its earlier cleanup finishes.
No runtime, shared Hugging Face cache or unrecorded staging sweep is added.

## Targeted validation and limits

`tests/PocketTTSStudioLite.ModelRemovalChecks` now includes 17 update scenarios
using production download, hash/cloning/shape validation, receipts, inventory,
locking and checked cleanup. HTTP and runtime diagnostics are substituted; all
model/metadata files live in isolated temporary storage. Cases include success,
gated authorization retry, credential-free CDN redirect, wrong hash/revision,
missing cloning tensors, changed shape, network failure, runtime/inventory change,
cancellation, installer contention, stale confirmation, crash-recovery/shared
references, busy postcommit cleanup, tampered receipts and changed configuration.
The existing 27 download-recovery and 17 removal/native-lifecycle checks also
pass; one separate symlink check is skipped for missing Windows privilege.

The 50 existing selection/persistence checks pass, covering replacement of active
objects, stale rows, unavailable aliases and saved preferences. Release x64 build
verification covers WinUI bindings/handlers; it does not prove dialog rendering.
No real model download, OAuth session, application launch, cloning/export,
installer run or user-data cleanup was performed. Real upstream model updates,
offline cloning/export after update and GUI interaction remain manual validation.
