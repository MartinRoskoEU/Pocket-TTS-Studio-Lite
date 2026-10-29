# Reference audio

Browse with the native Windows picker or drop one local audio file onto the
Reference audio card. Import, waveform generation, and playback use Windows audio
APIs and do not require an active model or an available Pocket TTS runtime. Runtime
identity refresh is advisory for an already loaded reference; only export requires
successful runtime/model verification. A new recording replaces the previous selection only after
successful decoding. The filename and real waveform describe the loaded recording;
play/pause, waveform click-and-drag seeking, elapsed time, duration, and repeated
playback use the same decoded samples. A failed import leaves the previous valid
selection available. Playback-device failures report an error without discarding
an otherwise valid reference.

The import/drop area is always present. The shared `AudioPlayerControl` starts
collapsed and becomes visible only after a reference has decoded successfully.
Clearing the control collapses its entire waveform/playback area, leaving no
reserved player space. There is no empty waveform placeholder. A waveform drawing
failure is reported by the renderer through the existing notification panel;
loading a previously collapsed control does not generate a premature waveform
warning before its first layout pass.

## Formats and validation

`ReferenceAudioFormats` discovers registered Windows Media Foundation audio source
handlers and available decoder transforms. Its centralized `Extensions` list drives
the picker's single Audio files filter, the secondary line inside the drop zone,
and case-insensitive extension validation for both browse results and dropped files.
The picker has no All files option or wildcard. It does not open if discovery returns
no supported extensions, avoiding the picker's default unfiltered behavior.
`FileTypeChoices` supplies the named filter because `FileTypeFilter` can append an
automatic All files category; see the [Windows App SDK picker specification](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/Storage.Pickers/Microsoft.Windows.Storage.Pickers.md).

DragOver asynchronously inspects storage items with an event deferral. Only one
local `StorageFile` with a supported extension receives Copy; unsupported suffixes,
folders (including folders named with audio suffixes), multiple items, non-file
contents, unavailable data, and busy import states receive None, leaving Windows
to display its native Not allowed indicator. Windows retains the original file
icon or thumbnail near the cursor and provides standard Copy feedback for accepted
drags or prohibited feedback alongside the dragged file for rejected drags. The app
does not override drag content, captions, glyphs, badges, or system cursors, and adds
no drop-zone indicators. Drop repeats the same validation
before entering the import path. Rejection does not pause playback, reset the
waveform, replace the reference, or invoke the decoder. Operation errors and warnings
use the shared [Voice Cloning notification panel](voice-export.md#voice-cloning-notifications).

Accepted inputs still use the same actual decoder without new magic-byte checks.
MIME/source-handler retries allow recognizable recordings whose extension is
supported but does not match their content. The decoder itself remains permissive
for direct callers; extension restrictions apply at the browse/drop boundary.
Raw unframed PCM handlers
are excluded because they cannot reliably identify arbitrary bytes as audio.

On the development machine the discovered extensions are:

`.wav .mp3 .flac .m4a .aac .adts .wma .amr .ac3 .ec3 .mpa .mka .weba .webm .mp4 .asf .3gp .3g2`

Availability depends on the installed Windows edition, source handlers, codecs,
and the codec inside each container. Ogg/AIFF source handlers were unavailable on
this machine; Opus and Vorbis inside WebM were successfully decoded. Other
registered formats are available only when their extension appears in the shared
supported list. Protected, empty, damaged, and undecodable recordings with supported
extensions still pass through the existing decoder error handling.
Windows N editions may require the Media Feature Pack.

## Preview and native export

The application uses the official [Media Foundation source reader](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-media-data-with-the-source-reader)
off the UI thread to decode the complete recording as mono float PCM at its decoded
source sample rate. The waveform is a min/max envelope of those samples. Windows
`MediaPlayer` consumes a seekable in-memory `MediaStreamSource`; ordinary import and
playback create no converted file. A read-only lease prevents the selected original
from being replaced or modified while loaded.

`export-voice` receives that original local path unchanged. The official runtime
decides whether it can decode it. If a completed native attempt specifically reports
an audio-decoder failure, the application retries once with an IEEE float mono WAV
containing the complete Windows-decoded preview at the same sample rate. There is
no trimming, model-rate resampling, fading, normalization, or appended silence. The
reserved `.safetensors` input suffix remains protected by the export backend's WAV
fallback for direct callers. Browse and drop reject that unsupported extension
before decoding. Existing voice profiles cannot be imported as reference audio.

The pinned v4.0.1 runtime (internal CLI version 3.3.0) reads audio using Symphonia,
averages channels, handles gapless MP3/AAC, selects the first 30 seconds, resamples
to the model's configured rate (24 kHz for the currently bundled configurations),
and performs its own end-of-prompt conditioning. These are observations of
[upstream audio code](https://raw.githubusercontent.com/kyutai-labs/pocket-tts/a922b912dfa366975294afb568d815b305acde5d/rust/src/audio.rs)
and [model code](https://raw.githubusercontent.com/kyutai-labs/pocket-tts/a922b912dfa366975294afb568d815b305acde5d/rust/src/model.rs),
not application rules. There is no application audio preprocessing contract,
version-specific processing table, or unknown-version preprocessing gate. Existing
runtime identity, `export-voice` interface, model compatibility, and verified private
asset checks remain. Changing the active runtime invalidates the loaded preview
and requires the user to reload the reference.

The preview represents the complete input recording, not an emulation of the
runtime's final conditioning. Windows and native decoders can differ in gapless
padding or channel mixing; the application deliberately does not imitate native
preprocessing. The runtime applies its own conditioning exactly once on the
successful export attempt.

## Lifetime and limitations

The player now lives in the reusable `AudioPlayerControl` UserControl. Reference
audio and generated Voice preview use this same waveform, playback, seeking, and
render-loop implementation with independent instance state. The imported source
and its fallback lifetime remain owned by the reference-import service; the control
owns only playback/render resources. Waveform and peak calculation are also shared
through `AudioPlaybackData`. See [generation and memory-only preview](voice-generation.md).

Fallbacks live only in an owned GUID directory under
`%LOCALAPPDATA%\PocketTTSStudioLite\reference-audio`, separate from model storage.
Normal replacement and close release playback, input leases, and owned fallbacks.
Generation sessions also keep their private `generation.yaml` in an owned directory
here, under the same safe cleanup rules. Generated speech itself is never written
to this directory or any WAV file; only the pre-existing reference fallback may
use an owned WAV when the runtime requires it.
Cleanup validates canonical paths, independent regular files, and parent directories;
it never sweeps other sessions or accesses Hugging Face caches. Unconfirmed native
termination or unsafe cleanup retains the uncertain fallback instead of forcing
deletion. Abrupt process termination can leave such private temporary files; no
automatic sweep of other sessions is performed.

Loading and waveform generation run asynchronously with cancellation checks.
Synchronous Windows decoder calls finish before cancellation can be observed.
Complete decoded PCM is kept in memory, so very long recordings require proportional
memory. A source whose format changes midstream must still negotiate a consistent
Windows preview format. Native errors that cannot be identified as decoder failures
are reported without speculative retry. A WAV retry may incur another model load.

Previous implementation validation (before the import crash fix): the Release
build succeeded with zero warnings and zero errors.

Validation: 36 targeted checks passed using synthetic local fixtures, including
VBR/ID3 MP3, PCM rates from 8 to 96 kHz, surround audio, installed containers/codecs,
misleading extensions, unmodified original input, a full 35-second preview,
unconditioned fallback samples, source/fallback write protection, cancellation,
invalid input, owned cleanup, and muted Windows playback/seek/end/replay. No GUI
application launch, model download, live inventory modification, or actual cloning
was performed. Picker/drop gestures, audible output, and end-to-end native export
with real model weights remain for manual validation.

## Browse/drop filtering verification, 2026-10-29

The application Release build passed with zero warnings and zero errors. All 61
targeted filtering checks passed using discovered Windows extensions, native storage
items, and synthetic audio in an isolated temporary directory. Coverage includes
case-insensitive matching of every discovered extension, unsupported/no/double
suffixes, empty lists, folders with audio suffixes, multiple/mixed items, an empty
format set, valid WAV decoding, corruption rejection, retention of an existing
reference, and decoder retries for a misleading supported extension.

Run only these checks with:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ReferenceAudioChecks\PocketTTSStudioLite.ReferenceAudioChecks.csproj --configuration Release -- --filtering-only
```

No application launch, GUI automation, playback test, or FFmpeg fixture generation
was performed for this change. Native picker presentation and drag cursor appearance
remain unverified interactively. Earlier audio checks remain available unchanged.

## Import crash investigation

The application's two crash dumps from 2026-10-28 at 18:54 show
`DrawReferenceWaveform`, `Path.set_Data`, and `ReferenceWaveform_SizeChanged`, with
`System.ArgumentException` / `0x80070057`. The renderer assigned one `GeometryGroup`
to both waveform `Path.Data` properties. WinUI geometries, including their children,
require separate ownership; see the [WinUI team's explanation](https://github.com/microsoft/microsoft-ui-xaml/issues/5780).
The initial assignment could be caught by the import handler, but a subsequent
layout `SizeChanged` repeated it outside that asynchronous error boundary and
terminated the application.

Both layers now receive independent geometry trees built from the same envelope.
Redraw errors are reported locally without discarding the decoded recording.
Runtime verification no longer runs in the shared import path or gates preview
controls; unavailable runtime metadata does not invalidate a valid Windows preview.
The original recording remains unchanged, and export retains runtime/model/CLI
verification. The supplied `audio (28).wav` has a conventional 48 kHz, mono, 16-bit
PCM RIFF structure with 12.8 seconds of audio; it was inspected read-only, not decoded
or played during this investigation. Media Foundation decoding and playback code
were reviewed; neither is implicated by the captured crashing call stacks.

This fix was reviewed against source, the WAV structure, Windows Application Error /
WER records, and the existing crash dumps. No build, tests, application launch, or
live import reproduction was performed, as requested. The corrected UI path still
needs execution validation in a later authorized session.

## Waveform interaction and rendering

The waveform itself is the seeking surface; there is no separate Slider or progress
bar. Pointer capture lets dragging continue beyond its edges, clamping seeks to the
recording's bounds. Current time and total duration share a compact horizontal row
with the existing Play/Pause button below the waveform. Routine ready/preprocessing
messages are omitted. Import disables conflicting controls while loading; cancellation
and actual errors use the page notification panel rather than an inline audio status.

Both players already shared the same 1024-bin min/max envelope calculation; there
was no special reference-audio amplitude sampler. Previously, Reference audio was
in a narrower column while Voice preview spanned the page. Drawing a peak bar for
almost every physical pixel made dense, transient-rich recordings look especially
busy at that smaller width. The recording's dynamics, background sound, duration,
and pauses still determine its actual shape; reference and generated speech should
not be forced to look identical. The UI-held clips were not compared acoustically.

`AudioWaveformDisplay` now aggregates the precise source-envelope bins into bars
spaced at two effective pixels, then applies a local three-bar weighted average
(1:2:1) to the displayed upper/lower amplitudes. Exactly silent aggregated bars stay
zero, preserving silence gaps, while sustained quiet and speech retain distinct
levels. The original envelope, peak, decoded PCM, file, and duration are untouched.
This is display-only smoothing in the existing shared player, not audio processing,
resampling, normalization, or separate reference/preview rendering.

The existing gray remaining audio, blue played clip, vertical playhead, and line
geometries remain. Each layer owns its own geometry tree. The trees are cached for
the current recording, dimensions, and DPI; playback and dragging only adjust the
clip and playhead transform. Resize rebuilds are limited to changes in those inputs.
Bar positions cover the same full recording timeline; seeking and playback position
still use exact sample-derived duration and Windows' playback clock.

Sixteen targeted checks cover density, smoothing, silence/quiet/speech distinction,
end transients, bipolar asymmetry, and unchanged source envelopes/PCM/duration:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ModelSetupChecks\PocketTTSStudioLite.ModelSetupChecks.csproj --configuration Release -- --waveform-only
```

The Release build passed without warnings/errors. These checks do not launch WinUI
or verify the visual appearance interactively.

Playback position follows Windows' playback clock on rendering frames rather than
a 50 ms timer. Drag targets are displayed on frames and coalesced to the latest
position; at most one native seek is in flight. Releasing the pointer retains and
commits the final target, including during paused playback. Completion, cancellation,
capture loss, audio replacement, and close release the relevant pointer/event state.
Rendering callbacks are detached when idle or when playback resources are released.
Audio samples, decoding, and export input are unchanged.

The initial UI and interaction change received source review only, as requested
at that time. Follow-up build and interactive verification are recorded below.

## Follow-up verification, 2026-10-28

The existing Release build initially reported CS1061 for the nonexistent
`PointerPoint.PointerDevice` member, followed by eleven cascading XAML type errors
(WMC0001/WMC0110) and warning WMC1509. Using the WinUI
`Microsoft.UI.Input.PointerPoint.PointerDeviceType` property and matching enum
resolved all of them without changes to converters or build configuration.

Both the standard Release build and the documented unpackaged Release build passed
with zero warnings and zero errors. The existing ReferenceAudioChecks program
passed all 36 targeted Windows audio checks; no new dependency or test infrastructure
was added.

The unpackaged application was tested interactively with
`C:\Users\rosko\Downloads\audio (28).wav` (12.80 seconds):

- Native picker import and waveform rendering succeeded without a crash.
- Play/Pause and resume worked; paused time and playhead remained stationary.
- Playback completed at 00:12.80 with the full waveform marked played.
- Paused click-to-seek reached approximately 00:03.20 at one quarter of the waveform.
- Paused dragging reached approximately 00:09.58 at three quarters.
- Clicking and dragging backward during playback moved the cursor, played clip,
  and current time together while playback remained active.
- Captured dragging beyond the right edge clamped to the end.
- That boundary exposed a Windows last-sample clamp: the time could show 00:12.79,
  and the next Play would finish that sample instead of restarting. Retaining the
  exact-end seek intent fixed both. The rebuilt app was retested: it shows 00:12.80
  at the end, then one Play press restarts at zero.
- Sampled screenshots show the cursor, played clip, and time advancing together
  (including 00:11.38, 00:11.59, 00:11.80, and 00:12.03) with an unchanged waveform.

No crash or UI freeze was observed in these checks. Source review confirms geometry
is rebuilt only for import/layout/DPI changes and is cached when those inputs are
unchanged; playback updates the clip and cursor. Automated drag gestures verified
resulting positions and responsiveness. High-frame-rate pacing, intermediate frames
inside a drag, and runtime geometry-rebuild counts were not instrumented, so absolute
smoothness or latency is not claimed. No voice cloning, model installation/removal,
or application dependency changes were performed.
