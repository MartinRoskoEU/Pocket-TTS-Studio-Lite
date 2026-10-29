# Private application-managed model storage

The only managed installation root is
`%LOCALAPPDATA%\PocketTTSStudioLite\models`, independent of `HF_HOME`,
`HF_HUB_CACHE`, `HUGGINGFACE_HUB_CACHE`, `HOME`, and `XDG_CACHE_HOME`.

```text
PocketTTSStudioLite/
  model-inventory.json                 # schema 2; authoritative model manifests
  model-inventory.previous.json        # atomic inventory backup
  model-update-checks.json             # existing identity-bound update results/times
  models/
    .operations.lock                  # shared readers / exclusive publication and registration
    .native-operations/<operation-guid>.json  # durable job/child reconciliation markers
    blobs/
      sha256/<lowercase-payload-sha256>
      git-blob-sha1/<lowercase-git-blob-sha1>
    .staging/<operation-guid>/<file-guid>.partial
```

Each blob is a regular independent file containing verified asset bytes. There
are no snapshot links, per-repository storage modes, or external installed file
references. Identical files with the same hash algorithm/hash use the same blob
path, across models and commits. Model manifests retain all logical repository
paths even when multiple references resolve to one physical blob. Every retained
record, including an Incompatible/Unverified model, contributes references for
future removal decisions; numeric reference counters are not needed to derive
the reference graph from a valid inventory.

## Installation and verification

The installer first verifies and reuses private blobs. It never searches shared
Hugging Face snapshots/blobs, imports external files, or reads global cache
settings to locate assets. External source paths cannot be registered or passed
through the asset verifier; neither symlinks nor hardlinks are created.

Missing assets download directly into the private operation's staging directory
from the same trusted official repository/commits. Space is checked for all
missing private bytes, and disk-full errors remain explicit.
The complete staged/private asset set must pass sizes, hashes, native config
binding, and voice-cloning tensor validation before publication. Every active
native file-path declaration must reference an inventoried asset; configurations
cannot retain hardcoded external model paths or unresolved remote references.

Verified staged blobs are moved without replacement to their canonical private
paths. Existing blobs are never overwritten, including corrupt/conflicting ones.
After final verification, registration rechecks files and atomically commits the
schema-2 inventory with its existing lock/flush/backup conventions. Only that
commit establishes installation. A crash/failure can leave verified unregistered
blobs for a later explicit Install to reuse; they do not unlock export or appear
as Installed. No orphan cleanup is implemented.

The installer cleans only files it created in its own operation directory and
removes that directory only if empty. Published blobs and other operation
directories are preserved. Interrupted/crashed-process leftovers are not swept.
Shared HF cache files, directories, refs, snapshots, and locks are never modified.

## Inventory and runtime use

Schema 2 retains alias, primary repository/commit, the fixed private installation
root, required file repository paths/commits/sizes/hash algorithms/hashes,
canonical private relative blob references, and mandatory native configuration.
Different manifests identify a shared file through the same private blob path.
Root equality, canonical hash-derived paths, relative-path validation, and
consistent sizes prevent arbitrary locations or conflicting references.

Installed status requires both an inventory record and verified private files.
Runtime aliases still define support; bundled metadata now supplies trusted
installation plans only, never existing shared-cache installation detection.
Registered private files retain Installed/Unverified/Incompatible semantics;
unknown runtime support never becomes Incompatible. A supported alias with no
record is Not installed, even when its bytes exist in a shared cache or as private
unregistered blobs. Unsupported registered models remain identifiable from their
retained manifests/configurations. No installation is auto-registered.

The private store rejects reparse points in model paths, verifies final opened
handle paths, and rejects files with multiple hardlinks. These Windows checks
apply to every asset opened for verification, including private staging.
Model verification/update checks and export take shared operation leases;
publication and inventory registration take exclusive leases. Acquisition waits
asynchronously with cancellation across processes. Downloads in unique
staging can proceed while an existing export reads immutable private files;
publication waits for readers. Export retains file and operation leases during
the native operation, and supplies only private paths through a native local
configuration. The old implicit English/cache-downloading export entry point is
removed; application voice export uses the same verified-model entry point.
Export strips inherited HF cache-location settings and tokens from the child
environment. Every active file reference is a verified private path, unresolved
remote references are rejected, and the non-cloning fallback is removed. The
native offline environment switch is not relied upon to prevent downloads.

Anonymous model update comparison and its existing persistent identities remain
intact. Identity fingerprints include private paths and configuration, so old
shared-path update results cannot apply to reinstalled private models. The
24-hour per-identity scheduler treats a new private installation as eligible.
Runtime validation/updating and OAuth Device Flow are preserved. Model removal
uses the existing UI notification card; model update installation is not added.

## Manual development cleanup before reinstalling

This replaces an unreleased storage architecture. There is no migration,
schema-1 reader, shared-path fallback, or automatic inventory conversion.
An old/unsupported inventory produces a clear error and is not overwritten by
startup, verification, installation, or update checks. Schema-2 records pointing
outside private storage likewise cannot establish installation.

Only an unsupported old inventory requires the following manual preparation;
an existing schema-2 inventory and its private files must be preserved:

1. Close all Pocket TTS Studio Lite instances and wait for their operations to end.
2. Archive/rename or remove
   `%LOCALAPPDATA%\PocketTTSStudioLite\model-inventory.json`. This is the required
   step; leaving its old entries in place blocks registration. Do not merely
   change its schema number or rewrite paths to pretend the files were installed.
3. Optionally archive `model-inventory.previous.json` alongside it to avoid
   accidentally restoring outdated development records. This backup is never
   automatically loaded and does not block reinstalling.
4. Preserve `%USERPROFILE%\.cache\huggingface\hub` and any configured shared HF
   cache. Preserve runtime identities/installations, audio, exported profiles,
   and `model-update-checks.json`; old result fingerprints will not match the
   new private installations. No cache or runtime cleanup is required.
5. Launch the new build and explicitly reinstall `english_2026-09`,
   `english_2026-09_2`, and `english_2026-10` as desired. Only verified private
   blobs are reused; missing files require downloads. New private schema-2 records
   appear only after success.

The application supplies no shared-cache cleanup or migration mechanism. Legacy
cache removal is a separate, explicitly authorized maintenance operation: check
that the application/native operations are stopped, validate exact paths and
independent private duplicates against hashes/sizes and the current schema-2
inventory, and reject links or busy files. Do not infer exclusive ownership from
old Lite registrations: other applications and tools can use the same cache.
Never clear the cache wholesale or change installed model records as part of
legacy cache cleanup.

## Uninstall Model

[Model removal](model-removal.md) atomically commits unregistration and optional
schema-2 `PendingCleanups` metadata, then deletes only canonical private blobs
unreferenced by every remaining entry, including Incompatible/Unverified models.
Installation and model operation locks coordinate removal with publication,
verification, activation, and native operations. Startup retries pending work
after rechecking references and native jobs.

Native invocations use durable markers and atomic job-associated creation;
unresolved descendants block removal. Deletion validates regular independent
files through exclusive handles and pins parent directories. Failures remain
pending; reported bytes exclude shared/missing/undeleted files. No external-cache
access belongs in model uninstall. Abandoned staging/unregistered orphan blobs
still need separate handling; no migration or garbage collector is supplied.
