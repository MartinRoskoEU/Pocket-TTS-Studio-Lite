# Runtime update checks

After the initial local model check finishes, `MainWindow` starts an asynchronous
`PocketTtsRuntimeUpdateService` check and retains its task in `RuntimeUpdateCheck`.
The Settings runtime card awaits that same task to display `PocketTtsRuntimeUpdateResult`.
The installed GitHub release tag is read through the existing verified local
identity API; the CLI's internal version is not displayed as the release version.

The full-width **Pocket TTS Runtime** card uses native WinUI controls and the
existing theme-aware Fluent card brushes, a 1-pixel border, 8-pixel corners, and
20-pixel padding. Runtime readiness (`Checking`, `Ready`, or `Error`) is separate
from update availability, so a failed network request does not change readiness.
The header uses a 17-pixel semibold title and the installed release below it with
4-pixel spacing. Readiness is a solid 12-pixel circle inset 12 pixels from the
right of the header content, centered across both title/release rows using Grid
alignment: themed green for ready, red for verification failure,
and gray when unknown/checking. There is no readiness text, spinner, border, or
background. A native tooltip and accessible automation name describe the current
status, including the existing error message when verification fails. The small
status control exposes a noninteractive image automation peer. A 1-pixel themed
divider has 16-pixel margins above and below. The update row uses 16-pixel
semibold status text, an 18-pixel icon with 10-pixel spacing, and the native button
at 36 pixels high with 16-pixel horizontal padding and 14-pixel text. Up to date
uses the themed success color/checkmark, available updates use the accent color,
and unavailable/incompatible checks use neutral/warning styling.

All card rows size to their content. The update row stacks its button below the
status below 480 pixels of card-content width. The separate Models card is
documented in [model setup](model-setup.md).

The update status displays **Checking for updates...**, **Up to date**,
**Update available: vX.X.X**, **Unable to check for updates**, or
**No compatible Windows runtime available**. **Check for updates** reruns the
existing service asynchronously, replacing `RuntimeUpdateCheck` with the new task.
The button is disabled during checks and overlapping requests are ignored. When
a compatible newer release is detected, it becomes **Update**. Clicking opens a
native confirmation showing the installed and available release tags, with Cancel
as the default action. Detecting a release never changes the installed version
label or claims that an update was installed. Startup never downloads or installs.

Before making a request, the service hashes the selected active executable,
verifies the trusted bundled pin or local installation receipt, and verifies the
persisted release/hash identity at
`%LOCALAPPDATA%\PocketTTSStudioLite\runtime-identity.json`. Missing, invalid, or
mismatched identity records skip the network request. This path only reads the
record; the existing model-setup persistence and runtime diagnostics are preserved.

The only release-metadata endpoint is
`https://api.github.com/repos/kyutai-labs/pocket-tts/releases/latest`, using
`HttpClient`, a `PocketTTSStudioLite/0.1.0` user agent, GitHub's JSON media type,
API version `2022-11-28`, and a 10-second network deadline. There is no connectivity
probe, retry, release-history lookup, authentication, or asset download. Closing
the window cancels the check. Responses are limited to 2 MiB.

Only `tag_name` and the locally stored `ReleaseVersion` are compared using SemVer
2.0 precedence: numeric major/minor/patch, prerelease identifiers, and ignored
build metadata, with an optional `v` tag prefix. Invalid semantic tags make the
check unavailable. Internal `--version` output and asset filename versions are
never used to identify or compare GitHub releases.

| Result status | Meaning |
| --- | --- |
| `UpToDate` | The latest tag is equal to or older than the installed release. |
| `UpdateAvailable` | The latest tag is newer and has a compatible uploaded Windows x64 Rust ZIP asset. |
| `NoCompatibleWindowsRuntimeAsset` | The latest tag is newer but has no compatible asset. |
| `Unavailable` | Identity verification, cancellation, timeout, offline operation, GitHub errors, or invalid metadata prevented the check. |

Compatible asset names follow the official
`pocket-tts-<version>-x86_64-pc-windows-msvc.zip` convention. Asset metadata includes
the GitHub asset ID, filename, official HTTPS download URL, byte size, and optional
GitHub digest. Metadata discovery alone does not prove CLI compatibility. The
update checker never changes the runtime, trusted pin, saved identity, or models.

## Confirmed installation

`PocketTtsRuntimeInstallService` is called only after confirmation. It takes an
exclusive local update lock, verifies the previous runtime, and reuses the latest
release service to refresh metadata. A changed release/asset or installed release
requires a new check and confirmation. It downloads only the confirmed latest
Windows ZIP, using anonymous HTTPS and an allowlist for GitHub asset redirects.
The card shows download percentage, verification/extraction/validation progress,
and a Cancel action. Conflicting model refresh, file pickers, and
exports are disabled during confirmation/installation. Closing cancels and waits
for owned-process/download cleanup; incomplete staging cleanup keeps the window
open to report the problem.

Installation requires the official GitHub `sha256:` asset digest. If it is
missing or invalid, installation aborts before downloading; a checksum is never
guessed or trusted from the executable's internal version. Content length and
streamed bytes must match the official asset size. Requests have a 30-second idle
timeout, the whole installation has a 15-minute deadline, and ZIPs are limited to
256 MiB. A verified ZIP is held read-only through extraction. ZIP extraction
accepts only `pocket-tts.exe` and `LICENSE`, flat or in one common directory, and
rejects traversal paths, links, duplicates, extra files, and oversized entries.
Only fixed application-controlled leaf filenames are written. Entry SHA-256 values
are calculated from the verified archive streams and checked against installed
files. Temporary files stay under the application's local runtime staging folder.

The candidate must be a Windows x64 PE executable on the existing supported
Windows x64/AVX2/FMA platform. Model-free `--version` and `export-voice --help`
diagnostics use the existing timeout/process cleanup logic. They must preserve
local `--config`, audio/output arguments, and the previous runtime's model aliases.
These checks do not execute inference or alter/download model files. The bundled
runtime retains its exact pinned hash and `pocket-tts 3.3.0` diagnostic check;
updated release identity always comes from GitHub's tag and verified ZIP.

Updates are published to
`%LOCALAPPDATA%\PocketTTSStudioLite\runtimes\<release-tag>\<executable-SHA256>\`.
Each directory retains the executable, upstream license, and
`runtime-installation.json` receipt with release/asset identity and archive,
executable, and license SHA-256 values. Directories are never overwritten.
Existing verified directories can only be reused if their receipt matches the
newly verified asset. Runtime updates do not alter private model storage or
enable shared-cache asset lookup.

After validation, activation atomically replaces `runtime-identity.json`, retaining
the same `ReleaseVersion`/`ExecutableSha256` format and saving the previous exact
record as `runtime-identity.previous.json`. The new active executable is reverified;
failure restores the previous identity by atomic rename. Cancellation is honored
up to this commit; the short commit/rollback section then finishes without being
interrupted. Services resolve the active runtime from this identity and verify its
receipt/hashes. Each export captures one verified executable, so switching the
identity cannot change an in-flight export. Previous runtime directories and the
app-relative bundled v4.0.1 fallback are never modified or deleted. If filesystem
failure prevents automatic identity recovery, the error identifies that failure
and retained records/files remain available for manual recovery after closing the
application. Ordinary integrity failures do not silently rewrite the identity.

On success the UI refreshes installed release, readiness/model checks, and latest
release status. A failed or cancelled installation reports its outcome without
claiming success or changing model files, authentication, or export formats.

Initial service verification: Release build and one direct latest-release API
request after verifying the local executable and stored identity. GitHub returned
`v4.0.1` and `pocket-tts-3.3.0-x86_64-pc-windows-msvc.zip` (7,914,990 bytes), matching
the installed release tag. No regression tests, new test infrastructure, downloads,
extraction, installation, or application launch were performed. Startup integration
and failure behavior were reviewed in source, without interactive verification.

Runtime card verification: Release build only; no regression tests or application
launch. Theme rendering, resizing, and interactive button behavior remain
unverified at runtime.

Installer verification: application Release build and source review of download,
verification, activation, cancellation, and rollback paths only. No runtime ZIP,
checksum file, or release was downloaded for this change; no installation or
application launch was performed. No regression tests or test infrastructure were
added or run. The existing voice-export check project only received source-link
updates for the runtime-selection dependencies and was not built or run.

Manual installation testing is left to the user: confirm the dialog and release
tags, Cancel during download/validation, try offline/failed downloads, and check a
successful installation retains previous files and refreshes the displayed tag.
Then verify existing local model selection and offline voice export with the new
runtime. Interactive progress, real archive layouts, filesystem failure recovery,
and end-to-end inference remain unverified.
