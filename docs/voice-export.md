# Voice Cloning and native voice profile export

The application now uses the [model-first workflow](model-setup.md). Startup
restores the last explicit usable selection, or activates the first usable model
when no selection preference exists. Removal preserves an empty selection. Settings
manages inventory without choosing the export model. The existing Model ComboBox
in Voice Cloning selects among installed, verified, runtime-compatible models through
the same activation backend. Choose reference audio and an export folder.
Incomplete/unverified installations cannot unlock export.
No model downloads or sign-in are initiated by this UI.

The navigation item and heading are Voice Cloning. The description is:

> Clone a voice from reference audio, preview generated speech, and export a reusable voice profile.

The page uses one full-width vertical flow at every window width: the notification
under the heading, Reference audio, Generate preview, Voice preview, then Export
voice profile. The native vertical StackPanel uses a 20-effective-pixel gap; cards
use Settings' 20-pixel padding, 8-pixel corners, theme brushes, and natural height.
The native ScrollViewer owns scrolling and respects Windows' scrollbar accessibility
settings. No page-specific resize handler or forced-scrollbar workaround is needed.

Generate preview retains the existing Model ComboBox and its selection/persistence
behavior. The editable multiline text has a visible Preview text header and grows
within a usable editing-height range rather than retaining a fixed height. It starts with:

> Adventurer, listen carefully: at dawn, three scouts vanished near the shattered watchtower. Search the ruins, retrieve the silver key, and protect the wounded prisoner before the seventh bell. But tell me—did you hear a strange whisper beneath the storm, or was it only the wind?

The Generate button uses the selected model, loaded reference, and editable
text for local speech generation. Generated audio stays in memory and uses the same
reusable player as Reference audio; see [generation and preview](voice-generation.md).
The text is not used for voice-profile export. Generation progress and cancellation
are in Generate preview so that the first generation remains visible before a
preview exists. Voice preview contains only its heading and the shared player;
the entire card stays collapsed until valid audio has been generated.

Export voice profile contains the existing Export folder field/picker, Export,
export progress, and cancellation. The folder button uses the native Windows App
SDK FolderPicker; there is no filename field or Save As step. Export reuses the
existing handler and all runtime/model/reference checks; it does not require Generate.
Permanent sidebar navigation replaces the former redundant Settings button.

Both sections use the same `AudioPlayerControl`, collapsed in its empty state.
Setting decoded audio shows it; clearing it collapses the whole control. There is
no placeholder waveform or stale preview-unavailable message. The reference drop
zone remains visible even after import, with the filename and player below it.
An empty filename is collapsed instead of displaying “No reference audio selected.”
Failed generation never clears or hides the last successful preview.

## Export folder persistence and availability

The [native Windows App SDK FolderPicker](https://learn.microsoft.com/windows/apps/develop/files/using-file-folder-pickers)
is associated with the current AppWindow. It is usable without selecting a model
or importing audio; an active conflicting operation still blocks it. No Save As
dialog is used. Folder selection does not require Generate.

`PocketTtsVoiceExportSettings` follows the existing application settings convention:
versioned JSON under `%LOCALAPPDATA%\PocketTTSStudioLite`, written through the shared
durable JSON writer and atomically published. `voice-export.json` stores schema 1
and `ExportFolder`, separately from installation inventory and model selection.
Each successful folder selection is saved immediately. Saving errors are reported;
the chosen accessible folder remains usable for this session without claiming
that its preference was persisted.

Startup restores the saved path and checks availability off the UI thread. It
never creates a missing destination or falls back to another folder. Malformed,
unreadable, missing, or nonwritable saved destinations leave Export disabled and
allow choosing a replacement. Missing settings simply leave the destination empty.
Access validation briefly creates an exclusive, automatically deleted probe file
to verify create/write/delete access without changing existing profiles. The same
check runs on selection and immediately before exporting; filesystem changes after
a successful check are handled by the existing export error/cleanup path.

Export requires the prepared reference, an active verified runtime-compatible model,
and a validated destination. Picker and Export handlers share their button guards,
including generation, model installation/removal/verification/activation, runtime
operations, cancellation/cleanup, and shutdown. Generated preview audio and Cloning
text are not inputs to voice-profile export.

## Automatic filenames and overwrite confirmation

Export constructs `<reference-audio-filename>_<model-alias>.safetensors` in the chosen
folder, removing the reference's last extension. For example, `reference.wav` with
`english_2026-10` becomes `reference_english_2026-10.safetensors`. There are no version
suffixes. Invalid Windows filename characters/control characters are replaced with
underscores; empty reference stems, reserved device names, trailing spaces/periods,
and the 255-character component limit are handled. Truncation preserves the model
alias/suffix and avoids splitting Unicode surrogate pairs. See Microsoft's
[Windows filename rules](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file).

If the file exists, Export displays a native WinUI ContentDialog showing that
filename, with Overwrite and Cancel buttons; Cancel is the default. Cancel returns
before pausing playback or starting the runtime and leaves the original unchanged.
Confirmation is captured only for this export attempt and exact destination.

New output is staged in the chosen folder through the existing verified export
implementation, including any WAV retry and model-configuration cleanup. Only a
successful result reaches final publication. A confirmed overwrite uses File.Replace
to atomically replace the existing file; an absent/unconfirmed destination uses
File.Move with overwrite disabled. Cancellation is checked immediately before
publication. Failure or cancellation before publication preserves the original;
replacement failures also leave it untouched. The staging source is consumed by
successful publication, with no later cleanup I/O that could turn a committed
replacement into an apparent failure. Unpublished staging files are cleaned up.

If a file appears concurrently after the existence check without confirmation,
publication fails without overwriting it; exporting again offers confirmation.
The successful-result notification reports the final user-visible path.

Verification: Release build passed with zero warnings/errors. All 35 targeted
filename/publication/layout checks passed, including Windows filename restrictions,
no versioning, unconfirmed collisions, cancelled publication, missing/locked outputs,
confirmed replacement, concurrent publishers, and the exact editable default text.
Run these without the GUI or native TTS runtime:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ModelSetupChecks\PocketTTSStudioLite.ModelSetupChecks.csproj --configuration Release -- --profile-export-only
```

All 50 targeted model-selection/persistence checks and all 27 notification checks
also passed. The ContentDialog, folder-picker interaction, and native inference
were not executed interactively. No application launch or GUI automation was performed.

## Voice Cloning notifications

A full-width Fluent notification card sits directly below the page heading and
description, above both columns. It shares Settings'
card, title-color, and dismiss-button styles: 20-pixel padding, 8-pixel corners,
theme-aware brushes, and the same 28-pixel transparent X button with a 14-pixel icon.
Success uses the green success brush, errors use the red critical brush, warnings
use the caution brush, and information uses neutral secondary text. The icon follows
the heading color. Heading and message wrap within the remaining width; the dismiss
button stays at the upper right. The page keeps its existing 20-pixel section spacing.

The panel is collapsed when inactive and contains one short heading and message.
Reference import/decoding and picker errors, playback/seek failures, waveform/cleanup
warnings, runtime-reference reload warnings, model activation/preference errors, and
export outcomes use it. Export success includes the destination. The WAV-fallback
note is appended to the export result rather than becoming a second notification.
Cancellation and destination selection use information notifications. The existing
audio exception handling, model activation, export validation, cancellation, and
temporary-file preservation behavior remain unchanged.

New events replace the visible notification. X dismisses it; repeated identical
callbacks do not reopen it. An explicit new picker/import/model-selection/export
operation clears prior notification state, allowing a fresh attempt to report the
same error. Empty reference status updates do not clear unrelated notifications.
Showing a new notification reveals it in the page's existing scroll viewer.
Navigation retains notification state without recreating or re-reporting it.

The former inline reference status is removed. Voice preview inline text is now
only active export/cancellation progress and is hidden with its controls when idle;
terminal results are not duplicated there. The Model ComboBox keeps its native
selection and placeholder; the old active-model information label is removed.
No new dependencies or audio-processing changes are introduced.

Verification: the Release build and 27 focused notification checks passed with
zero warnings/errors. The checks cover lifecycle, replacement, dismissal, duplicate
reports, new attempts, every export failure category, combined fallback results,
and XAML placement/shared-style/inline-status contracts. All 61 existing targeted
reference-audio filtering checks also passed. Run the notification checks with:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ModelSetupChecks\PocketTTSStudioLite.ModelSetupChecks.csproj --configuration Release -- --notifications-only
```

No application launch or GUI automation was performed. Actual theme rendering,
responsive wrapping, accessibility announcements, scrolling, and dismiss-button
interaction remain unverified interactively.

## Native export behavior

Reference audio supports asynchronous one-file browsing/replacement and drag and drop,
Windows Media Foundation decoding, waveform display, and local play/pause/seek with
accurate duration and position. Voice preview uses a second instance of this same
player with independently generated in-memory audio. See
[reference audio](reference-audio.md) for format discovery and verification limits.

`PocketTtsCliService.ExportPreparedReferenceAsync` invokes the validated Rust runtime
using `ProcessStartInfo.ArgumentList`, no shell, and a temporary native config.
The Windows launcher preserves these arguments and redirected UTF-8 streams
while atomically assigning the process to a kill-on-close job before it runs:

```text
pocket-tts.exe export-voice --config <local-config.yaml> <local-audio> <temporary-output.safetensors>
```

The pinned [official export implementation](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/main.rs#L293-L303)
loads the model, prepares the voice from the reference, and serializes the native
voice state. It does not need speech generation or a prepared server session. The
application reuses this existing command path without custom conditioning or
conversion. Native process completion and output checks, configuration/process
cleanup, and final publication all precede the success notification.

The retained native config is resolved to verified private model blob paths and its
non-cloning fallback removed. Unresolved remote references are rejected. Assets
are reverified and held open read-only until export ends; the owned config is
removed afterwards. Configs contain no tokens and every model path must bind to
an inventoried private asset. Export holds a shared model-store operation lease,
so private publication/registration waits for active readers. The earlier implicit
English/shared-cache export API and token handoff are removed; installation owns
OAuth exclusively. Export child environments strip ambient Hugging Face tokens
and cache-location settings. Every asset verification and generated config path
enforces private storage; no external-source verification bypass remains.

Audio and output stay local. The app sends the original validated recording to
`export-voice` unchanged. On a classified native audio-decoder failure, it can retry
once with a private Windows-decoded float WAV containing the complete preview PCM
at the recording's decoded sample rate. The reserved `.safetensors` input suffix
also requires that fallback after Windows has identified real audio, so the CLI
cannot mistake a mislabeled recording for a voice state. The application does not
trim, fade, resample to a model rate, or add silence to the CLI input. Pocket TTS
owns that processing. Unknown future internal preprocessing does not block export;
existing verified runtime, CLI, supported-model, and private asset checks remain.
Changing the verified active runtime requires reloading the reference preview.

Output is staged beside the destination and published unchanged only after exit
zero and nonempty output. Existing destinations are replaced only after explicit
overwrite confirmation and successful completion of the export/cleanup path.
Exported-profile interoperability with other models has not been verified.

Execution and stream draining remain asynchronous. The diagnostic 15-second
timeout is unchanged and does not apply to export. Progress is indeterminate;
raw export logs are consumed/classified/discarded to avoid credential exposure.
Cancellation kills the owned process tree and awaits cleanup. Aggregate tree
termination failures report incomplete cleanup. Window close waits for an active
export to stop; an unconfirmed cleanup keeps the window open.

Every native invocation holds a private operation reader lease and a durable
job marker; descendants must stop before the marker is cleared. Removal refuses
active/unresolved native operations. Export rechecks the exact current inventory
registration under its reader lease, preventing stale selections after removal
in another window. See [model removal](model-removal.md).

The v4.0.1 release artifact reports version 3.3.0. Its locked `hf-hub` 1.0.0
supports native pinned snapshot caching and token environments/files, but has no
independent model-install command or reliable `HF_HUB_OFFLINE` switch. Default
remote configs can attempt authenticated downloads or fall back to non-cloning
weights. Verified private-only configs avoid these paths. Installation uses the
application installer; no model update installation or cache cleanup is provided.
See [private storage](private-model-storage.md) and [runtime validation](pocket-tts-runtime.md).

The private storage replacement was verified by an application Release build and
source review only. No regression tests, launch, authentication, model downloads,
actual installation, or voice export were performed. Earlier checks target
the former storage/API contract and are not evidence for this replacement.
Private installation, offline export, file/operation locking, cancellation, and
interoperability remain for manual validation after the development inventory is
cleaned up as documented in [private storage](private-model-storage.md).

### Current completion verification and limits

The pre-release cleanup passed a Release build with zero warnings/errors and reran
44 profile publication/layout, 33 folder persistence/access, 34 generation/memory/player
structure, 27 notification, and 16 display-waveform checks. The updated layout
contracts cover page copy, single-column card order/full-width alignment/natural
height, Preview text labeling, visible generation
progress outside the initially hidden preview, and unchanged export/import wiring.
Waveform checks cover reduced visual variation, width-based density, silence gaps,
quiet/speech distinction, transients, and unchanged source envelopes/PCM/duration.
No GUI was launched or automated; live resizing, themes, and pointer interaction
remain unverified interactively.

See the [0.1.0 pre-release review](release-readiness-0.1.0.md) for the complete check
results and remaining legacy-test and distribution concerns. The earlier UI/folder
completion results below are historical verification of that change.

The UI/folder completion change passed a Release build with zero warnings/errors,
35 filename/publication/layout checks, 33 isolated folder persistence/access checks,
32 generation/memory/shared-player checks, 6 independent in-memory Windows player
checks, and 27 notification checks (133 targeted checks total). Folder tests
cover restart restoration, changed selections, missing folders without recreation,
actual ACL-denied writes, malformed settings, cancellation, and locked-settings
replacement with original preservation and staging cleanup. Publication tests cover
confirmed replacement, unconfirmed/concurrent collisions, locked/missing output,
and cancellation without damage to an existing file.

No application launch or GUI automation was performed. Native picker/dialog
interaction, actual UserControl visibility/layout, and a real native profile export
were not exercised in this change. Existing export verification/processing was
retained. Local write/delete access is required; network shares are unsupported.
Atomic replacement depends on the destination filesystem supporting `File.Replace`;
failure reports an error rather than falling back to destructive overwrite. The
pinned Rust runtime uses CPU inference and native preprocessing, including its own
reference handling. Cross-model profile interoperability remains unverified.

## Model selection

The existing ComboBox shows a filtered view of the same checked model entries used
by Settings. Entries must be InstalledVerified, explicitly supported by the active
runtime, and bound to the setup service's current verified model object. Not installed,
Unverified, Incompatible, unknown-compatibility, missing-model, and stale entries are
excluded. There is no second inventory or separately maintained catalog.

Entering Voice Cloning runs the existing asynchronous runtime/private-model check.
Settings installation, removal, and runtime refreshes update those same rows and
choices. A refresh preserves the current alias when still usable, rebinding to its
freshly verified model object. An unavailable active model clears selection without
silently choosing a replacement. With no choices, the ComboBox and Export are
disabled. The active-model information label is no longer displayed.
Startup restores the last explicit ComboBox choice only from the checked usable
models. `%LOCALAPPDATA%\PocketTTSStudioLite\model-selection.json` stores schema 1
and `SelectedModelAlias`, separately from installation inventory. A missing file
retains first-run activation of the first usable model; a saved null alias means
the user must explicitly choose a model. Removing an active or saved model writes
that empty state, including removal of an automatically activated first-run model.
Removing another model preserves the preference. An unavailable saved alias stays
inactive without fallback; a later explicit choice replaces it. Invalid or unreadable
preferences also prevent automatic activation and report an error. Failed writes
are reported rather than silently claiming the preference was saved.

Changing the selection awaits the existing activation method, which rechecks the
exact inventory registration under the shared operation lock. Export and conflicting
model actions cannot begin through the UI while activation or verification is pending.
Activation failure clears the selection, excludes the failed choice until refresh,
and reports an error in the page notification panel. The export handler captures
`PocketTtsModelSetupService.ActiveModel` only after activation finishes; its existing
runtime/model/configuration/asset checks remain. Reference audio and destination
selections are preserved when changing models.

Targeted automated verification:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ModelSetupChecks\PocketTTSStudioLite.ModelSetupChecks.csproj --configuration Release -- --selection-only
```

All 21 existing selection checks passed using in-memory checked-entry fixtures: compatibility/state
filtering, switching active models, selection preservation across refreshed objects,
stale-row rejection, clearing removed/unverified/incompatible selections, deactivation,
and empty results. An additional 29 persistence checks passed with isolated temporary
settings: first-run behavior, restart restoration, navigation reconciliation,
missing/unverified/incompatible/unknown models, removal of active and saved aliases,
removal of another model, explicit replacement, cancellation, and invalid/unwritable
settings. This mode starts no GUI or native runtime and accesses no live
inventory/model files. The test project now targets `net10.0-windows` because its
linked services and normal setup checks are Windows-specific. The SDK-generated
assembly platform annotation resolves all seven previous CA1416 warnings: six
registry accesses in `ReferenceAudioFormats.Discover` and COM release in
`MediaFoundationAudioDecoder.DecodeStream`. A non-incremental Release build passed
with zero warnings and zero errors, and all 50 selection/persistence checks passed.
No tests were removed, guards or suppressions added, or analyzers disabled.
See Microsoft's [CA1416 platform-target documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1416#tfm-target-platforms).
The application Release build passed with zero warnings and zero errors. No
application launch, computer-control automation, or interactive GUI testing was
performed for model selection.
