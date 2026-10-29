# Model installation

Settings > Models enables **Install** only for an alias supported by the verified
active runtime, with a complete embedded configuration and trusted, immutable
repository metadata. Installed/incompatible models cannot be installed again.
Unregistered verified private blobs can be reused during an explicit installation.
Shared cache contents alone never establish installation. Registered models needing repair remain Unverified; replacing or
repairing an existing inventory record and installation of model updates are not
implemented. Registered models support [confirmed removal](model-removal.md) with
shared-file protection and recovery. Manual anonymous [update checks](model-update-checks.md) are
available separately and never change installed files or inventory. Unknown runtime compatibility never enables installation.

`PocketTtsModelInstallPlan` resolves only the active `weights_path` and
`tokenizer_path` dependencies of the bundled native YAML. Unresolved remote
references block installation. The optional non-cloning fallback is excluded by
the existing local-config logic. No repository search, branch-head lookup, whole
repository download, dummy inference, or runtime install command is used.

## Metadata boundary

Network downloads originate exclusively from `kyutai/pocket-tts`, at exact
40-character commits from bundled trusted repository snapshots. The 25 bundled
configurations originally reference tokenizers in
`kyutai/pocket-tts-without-voice-cloning`. Each currently has a corresponding
file proven by embedded `kyutai/pocket-tts` metadata with the same repository
path, size, hash algorithm, and hash. Installation substitutes only these proven
references in its retained configuration, preferring the weights commit when
possible. The bundled YAML and raw metadata remain unchanged. Per-file source
commits are recorded; matching names alone never establish equivalence.

Future runtime aliases without such complete trusted configurations/snapshots
remain disabled. The current catalog is pinned reference metadata, not a live
catalog for arbitrary future runtime versions. A changed upstream file at a
pinned URL fails integrity verification. Metadata alone cannot prove the
voice-cloning tensors: the actual downloaded weights must also pass the existing
cloning and offline configuration checks before registration.

## Authentication and transfer

Every Install attempt starts anonymously. If the official repository requires
authentication, `MainWindow.ModelInstallation` automatically starts the existing
OAuth Device Flow through `InstallationAuthenticationDialog`, a native WinUI
`ContentDialog` attached to the installation window. It displays the public code,
expiry, authorization address, browser fallback, and a Cancel installation button.
There is one interactive authentication retry, with no separate OAuth protocol
implementation or persistent authentication section in Settings.

The dialog contains a clearly visible link to
[kyutai/pocket-tts repository conditions](https://huggingface.co/kyutai/pocket-tts)
and explains that signing in does not accept these conditions. The user must
review and accept them on Hugging Face when required, and then complete browser
authorization. Sign-in does not itself grant repository access. Startup,
local refresh, manual model update checks, and local export never initiate this
installation/login flow.

`PocketTtsModelInstallService` uses asynchronous streaming with 30-second
header/read inactivity deadlines, bounded HTTPS redirects, byte progress, and
cancellation. Redirects are limited to Hugging Face's `huggingface.co`/`hf.co`
domains and subdomains used for CDN/Xet delivery. Bearer credentials are sent
only to the official repository's resolve/resolve-cache paths on
`huggingface.co`, never to CDN hosts or other repositories. Tokens, raw provider
bodies, signed download URLs, and transport exception payloads are never logged
or displayed. The token is held only in memory for the current installation.
Cancellation immediately clears the local session, including when the dialog is
closed. A `finally` block clears it after success, authentication failure, or any
installation failure, before the model list refreshes. Each subsequent installation
starts without a token. Clearing the app session never signs out the browser,
revokes a browser session, or deletes Hugging Face browser cookies.

Authentication failures, missing access/conditions, HTTP/network failures,
incomplete transfers, cancellation, disk exhaustion, integrity errors, private blob
conflicts, runtime changes, and inventory commit failures have separate results
and safe user-facing messages. No independent connectivity probe is performed.

## Private storage, verification, and commit

All installed assets live exclusively under
`%LOCALAPPDATA%\PocketTTSStudioLite\models`. Canonical paths are
`blobs/sha256/<payload-sha256>` or `blobs/git-blob-sha1/<git-blob-sha1>`.
Identical private content is reused across models/commits; references come from
schema-2 `model-inventory.json`, whose native configuration is mandatory. Only
registered and verified private files can establish installation.

Only existing verified private blobs are reused. Shared HF snapshots/blobs are
never searched, imported, or written. Global HF cache settings do not locate
assets or select private storage. External paths and symlinks/hardlinks are
rejected by the asset verifier without an external-source bypass.

Missing files download directly into private `.staging/<operation-guid>` from
the official pinned repository. Space accounts for all missing private bytes.
The complete staged/private model passes size/hash,
configuration binding, and cloning-tensor checks before immutable no-overwrite
publication. Model-store reader/writer leases coordinate verification, export,
publication, and registration across processes. Registration rechecks files and
uses the existing atomic inventory writer/backup. No incomplete installation is
registered. Cancellation before commit leaves no record; cancellation after a
successful atomic commit does not falsely report failure. Cleanup touches only
this operation's temporary files/empty staging directory. Published unregistered
blobs remain reusable, with no automatic cleanup or Installed classification.

See [private storage](private-model-storage.md) for path/handle/link protections,
operation coordination, inventory invariants, limitations, and exact
[manual development cleanup](private-model-storage.md#manual-development-cleanup-before-reinstalling).
Unsupported development inventories are never migrated, converted, or silently
accepted. They must be manually archived before reinstalling. The application
leaves shared HF files untouched and never uses them as installation sources.

One installation runs at a time, including sign-in, with an application-instance
guard and cross-process installation lock. Refresh and conflicting runtime
installation are disabled while it runs. Closing cancels and awaits cleanup.
Read-only runtime update checks and existing voice export behavior are retained.

Success is shown only after full verification and registration. Settings then
refreshes the model list to Installed, without activation or navigation. Existing
startup restores a saved choice only when usable, and activates the first usable
model only if no selection preference exists. A saved empty selection after removal
requires an explicit choice in Voice Cloning. After successful installation, a separate nonblocking anonymous update
check considers the new inventory identity even if other models were checked
within the last 24 hours. It never downloads or installs updates. Selecting a
different export model uses the Model ComboBox in Voice Cloning.

## Installation progress and notifications

Settings displays one full-width installation card between Pocket TTS Runtime
and Models. It uses the same theme-aware Fluent background/border, 8-pixel
corners, and 20-pixel padding. The card is hidden until an installation starts,
then shows **Installing model**, the alias, the real current operation (including
downloaded/total size when reported), a full-width determinate or indeterminate
progress bar, and a bottom-right **Cancel installation** button. Cancellation
availability continues to follow the installer's existing `CanCancel` state.

The same card becomes a result notification after the workflow finishes:

- Success: a green checkmark, **Installation completed**, and
  **[model name] was installed and verified.**
- Failure: a red error icon, **Installation failed**, and the existing safe,
  user-friendly installation error.
- Cancellation: a neutral icon, **Installation cancelled**, and the existing
  cancellation message.

Results hide the progress bar and Cancel button and provide a transparent,
borderless Close icon: a 14-pixel Fluent glyph in a 28-by-28-pixel target, centered
beside the complete title/description block within the existing 20-pixel right
padding. Native hover, pressed, and focus states remain available. A result
remains until dismissed or replaced by another Install
attempt. Progress and results update this same card; no status controls or unused
installation rows remain below the Models list. The access-conditions link is
present only in the unchanged authentication dialog, not in the progress card.

Starting installation brings the card into view using the existing Settings
page scroller. Updates continue if the user visits another page; returning to
Settings brings the current active/result card into view without restarting
installation or creating another card. Progress updates do not repeatedly scroll
the page. Wrapped text, Auto-sized Grid rows, and the shared page scroller avoid
fixed-height clipping and nested scrollbars. No installation service, OAuth,
download, verification, registration, or cancellation behavior is changed.

## Development verification and manual validation

Only the application Release build and source review were performed for this
feature. No tests/test infrastructure, application launch, authentication,
model downloads, or real installations were performed. The user must manually
validate first installation and login, repository conditions/access denial,
progress/cancellation, offline/interrupted downloads, private blob reuse, disk/storage
errors, inventory publication, and subsequent offline startup/export. No model
weights are bundled and no new dependencies were added.

The progress-card redesign was verified by a Release build and source review
only. No regression tests or application launch were performed. Card visibility,
scrolling/page changes, theme rendering, progress, dismissal, and actual results
remain for manual validation with a real user-requested installation.
