# Model setup

The initial screen checks the application-relative official runtime and registered
private model files, without internet access, authentication, downloads, or asset
changes. Shared Hugging Face cache files are not installation evidence. Only an installed, verified, runtime-compatible model can be activated.
Export stays disabled until activation; reference audio import/playback is independent.
Activation is checked each session; explicit ComboBox choices are saved separately from inventory.

After startup verification, the last explicitly chosen model is restored only if
it remains installed, verified, and runtime-compatible. With no saved preference,
the first usable model is activated through the existing activation service.
Removing the selected model saves an empty selection; subsequent startups require
an explicit replacement. An unavailable saved alias never triggers fallback.
Voice Cloning opens after activation; otherwise Settings opens. Automatic activation
failures return to Settings and display the existing setup error status. The Model
ComboBox in Voice Cloning uses the same activation service to choose another usable model.
Its checked choices refresh on entry and preserve a still-valid active selection.
Settings contains no selection controls or **Use Model** action. **Check for updates**
first rechecks local inventory without activating another model or changing pages,
then performs the manual [anonymous model update check](model-update-checks.md)
for registered installed models only.

## Models management card

The full-width **Models** card uses the existing Fluent card background/border,
8-pixel corners, and 20-pixel padding. Its header says "Manage installed and
supported voice models." and offers **Check for updates**. Native `ItemsRepeater`
rows share the page's vertical scrolling, with no selection highlight, details
panel, fixed list height, or nested scrollbar. Each row shows the identifier,
status glyph/text, and contextual actions. Install is enabled only with complete
trusted metadata and known runtime support; Remove is enabled for all registered
models with native confirmation. Identifiers/statuses stack
in narrow layouts, while action buttons stay compact.

The service combines active-runtime aliases with private inventory records,
deduplicated by identifier. Bundled metadata supplies trusted install plans only;
it no longer detects shared-cache installations. Supported unregistered models
remain Not installed even when private unregistered blobs or shared HF cache files
exist. Explicit Install reuses only verified private blobs and downloads missing
files directly into private staging. No online-only candidates or arbitrary folder names
are added to the list.

## Persistent model inventory

`PocketTtsModelInventoryService` stores schema-2 JSON at
`%LOCALAPPDATA%\PocketTTSStudioLite\model-inventory.json`. The fixed installation
root is `%LOCALAPPDATA%\PocketTTSStudioLite\models`, independent of global HF
settings. Records retain aliases, repository/commit identities, every required
source path/size/hash, canonical private blob references, and mandatory native
configuration. Shared files use the same content-addressed path across manifests.

All record paths must match their expected hash-derived private blob paths.
Private files must be independent regular files: reparse points, external paths,
and hardlinks are rejected. Hash/size checks always run, and supported models
must pass native configuration and cloning-tensor verification. Configuration
file declarations must bind to inventoried private assets. No shared-cache or
bundled-configuration fallback is available for installed records.

Missing inventory is empty and is not created by verification. Unsupported
schemas, malformed documents, and duplicate identifiers are reported and never
overwritten; the current development schema-1 inventory requires manual cleanup,
not migration. Invalid individual schema-2 records remain Unverified. See
[private model storage and development cleanup](private-model-storage.md).

The installer registers only fully verified private assets, using new-only atomic
publication with inventory locks, flush, and `model-inventory.previous.json` backup.
Reader/writer model-store leases protect verification/export versus publication,
registration, and removal. [Model removal](model-removal.md) atomically unregisters
and journals cleanup before deleting unreferenced private blobs, with startup
recovery and update-result invalidation. No ownership migration or orphan garbage
collector is implemented. Active runtime validation and native export remain intact.

After a runtime update, the inventory continues supplying identifiers, commits,
locations, and expected file hashes even when those aliases/configurations no
longer appear in the runtime or bundled metadata. Verified files with an absent
runtime alias are Incompatible. Missing/changed recorded files stay visible as
Unverified, and unknown runtime compatibility is never inferred as Incompatible.

After the startup check finishes, a separate background [runtime update check](runtime-update-checks.md)
may request GitHub's latest-release metadata after verifying the saved runtime
identity. It does not delay navigation or require connectivity for local use.

## Runtime and model identity

The Settings page presents runtime information in a full-width Fluent card titled
**Pocket TTS Runtime**. Its installed release label comes from the existing verified
local identity, and its inline readiness indicator remains driven by model setup diagnostics.
Update availability and manual checks are independent; see [runtime update checks](runtime-update-checks.md).

Both Debug and Release copy the validated development runtime and its license to
`runtime/pocket-tts/` beside the application. The source directory defaults to the
repository's ignored `development/runtime/...` location and can be supplied with
`-p:PocketTtsRuntimeSourceDir=<directory>`. Builds reject a source executable with
a different SHA-256. Missing runtime files produce an explicit setup error; no
machine-specific absolute path, PATH search, or environment override is required.
The service's explicit constructor override remains available for callers/tests.
Normal path resolution selects the verified active local runtime recorded by
`runtime-identity.json`; bundled v4.0.1 remains the pinned fallback. Updated
runtimes retain per-installation release, archive, executable, and license hashes.

Runtime identity checks hash the actual executable, require the existing trusted
SHA-256, and then save `ReleaseVersion` (`v4.0.1`) and `ExecutableSha256` in
`%LOCALAPPDATA%\PocketTTSStudioLite\runtime-identity.json`. The project had no
application settings store; this uses its existing .NET file/path APIs and the
per-user local application-data folder, separate from model assets and app files.
Later checks rehash the executable and compare the saved release/hash against
the trusted bundled pin or a verified local installation receipt. Ordinary checks
never overwrite identities; invalid or mismatched records fail explicitly.
Only an explicitly confirmed runtime installation can atomically replace the
identity after validation, retaining a previous-identity backup for rollback.
Both startup checks and pre-export runtime validation use this identification.
Release identity comes from the pinned release/hash pairing, never from the
executable's internal `--version` output. Existing version/help and CPU diagnostics
remain unchanged.

Setup checks the selected executable's trusted hash, Windows x64/AVX2/FMA, version
diagnostics, and native local-config export-help contract. Bundled v4.0.1 still
requires its exact pin and reported CLI version `3.3.0`; updated runtimes use their
verified executable hash and must preserve the required export contract and model
aliases. Supported aliases come from that executable. An alias is not proof of
model installation. Relative HF cache paths are resolved from the bundled-runtime
base directory only for optional read-only copy-source lookup. Private installed
model paths never depend on HF cache settings or the active runtime location.

`RuntimeMetadata/v4.0.1` contains the 25 unmodified official YAML configurations
from source commit `a922b912dfa366975294afb568d815b305acde5d` and 12 raw official
Hub model-info JSON snapshots for their pinned primary weights/tokenizer revisions.
These are offline reference metadata, not weights, a custom download manifest, or
a new cache. Config provenance:
[release-pinned configs](https://github.com/kyutai-labs/pocket-tts/tree/a922b912dfa366975294afb568d815b305acde5d/pocket_tts/config).
Each JSON records its official repository `id`, immutable `sha`, file sizes and
Git/LFS hashes, from `https://huggingface.co/api/models/{id}/revision/{sha}?blobs=true`.
Runtime updates do not change this pinned model configuration/asset metadata.

States:

- **Installed:** supported by the detected active runtime; official pinned
  sizes/hashes match, the configuration is local, and weights contain the required
  voice-cloning encoder and speaker projection tensors. Green checkmark;
  **Remove** is enabled with confirmation.
- **Not installed:** supported alias without a registered private installation.
  Neutral text; **Install** is enabled when trusted metadata is complete. A recorded private installation with missing
  files is instead Unverified, because its recorded installation needs repair.
- **Unverified:** registered private model with missing files, invalid private references,
  wrong hashes, unresolved configuration, or missing cloning tensors. Warning
  styling; existing registrations cannot be repaired/replaced by Install, but
  **Remove** is enabled with confirmation. Unsafe ownership metadata blocks
  removal instead of guessing at file ownership.
  If runtime detection fails, hash-verified local files also appear
  here with `IsRuntimeSupported = null` and an explicit compatibility-unknown detail;
  they are never guessed to be incompatible.
- Registered models with missing/changed files or invalid file metadata remain
  **Unverified**, including when the runtime no longer supports their alias.
  Supported registered models lacking a usable native configuration are also
  **Unverified**. Records are not silently removed.
- **Incompatible:** all pinned local file identities are verified, but the alias
  is absent from a successfully detected runtime alias set. Orange warning and
  enabled **Remove** action with confirmation. File identity establishes local installation only;
  no voice-cloning compatibility is claimed.

Only Installed entries from the current successful check carry an activatable
model object. Activation rejects stale/incompatible/unknown entries, and export
also rechecks that the captured runtime supports the model alias before using
its assets. Existing export size/hash/cloning-tensor checks remain required.
The first eligible entry is still activated at startup; manual refresh preserves
the active identifier if it remains eligible and clears it otherwise. Model
installation is explicit and never activates another model or navigates. Removal
is explicit with native confirmation; installation of model updates is not implemented.

This verifies the deployment contract; it is not evidence of a completed inference
or exported-profile interoperability test. Private model storage is independent
of all HF cache settings. Shared caches are never searched, imported, modified,
or accessed by model removal.

The former hidden **Discover online variants** Settings control and its UI handler
are removed. The independent anonymous discovery backend remains available but
is not called by this card, startup, or refresh and never feeds this inventory.
Local model initialization/refresh itself makes no Hub requests. After startup
verification and navigation finish, a separate background check uses anonymous
metadata for registered installed identities due under the 24-hour interval.
Successful installation queues another eligibility pass for new identities. The
Models button refreshes locally and forces a manual check regardless of the
interval. Cached results require matching inventory identity and verified files;
update results never change installation/compatibility or block Voice Cloning.

## Offline export and installation

Export rechecks the exact runtime and selected asset hashes, holds read-only asset
leases, and derives a temporary native YAML config containing only local paths.
The optional non-cloning fallback is removed; unresolved remote references block
export. The native command is `export-voice --config <local-config> <audio> <output>`.
There is no 15-second export timeout. Existing process cancellation/cleanup remains.
Only verified private-model export is supported. The implicit English/shared-cache
export entry point is removed. No authentication is needed for private assets.

The official CLI has **no independent install command**. The separate
[authenticated installer](model-installation.md) downloads only the exact required
files at trusted pinned commits, verifies the complete model, and commits its
inventory record before reporting success. It never simulates installation with
inference/server startup. A native installation dialog automatically runs the
existing OAuth flow only when an anonymous download needs authorization. Settings
has no persistent authentication controls; the in-memory session is cleared on
every installation outcome. Sign-in is not part of startup or local export.
Registered models can be removed with reference-checked private cleanup and
recovery. Installation of model updates is not implemented.

Installation progress and dismissible results appear in a separate Fluent card
between Runtime and Models, rather than below the model list. Its real progress
and existing Cancel action continue across page changes. The card is brought into
view when installation starts or Settings is revisited; results remain until
dismissed or replaced. Repository conditions are linked only in the authentication
dialog. See [progress and notifications](model-installation.md#installation-progress-and-notifications).

## Verification and manual test

The installation feature was verified by an application Release build and source
review only. No tests, application launch, authentication, downloads, or real
installation were performed. See [installation verification](model-installation.md#development-verification-and-manual-validation)
for the manual validation still required. Existing startup activation, local
verification, offline export, theme rendering, and responsive layout should be
checked alongside the user's first real installation.
