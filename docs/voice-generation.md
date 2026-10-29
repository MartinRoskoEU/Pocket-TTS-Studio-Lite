# Local generation and in-memory preview

Generate uses the selected installed, verified, runtime-compatible model, the loaded
reference, and the editable Preview text in Generate preview. It is enabled only when these are usable
and no conflicting operation is running. Speech generation, voice conditioning,
text handling, and preprocessing belong to the official Rust runtime. The app does
not reproduce its inference or conditioning rules. Final Export still creates a
native voice profile from the reference; it does not export the generated preview.

## Confirmed official interfaces

The SHA-256 of the local v4.0.1 release executable was verified against the pinned
`813CABFC2F8FC7A4E5310DEDA6025426B3164610DD9F3C3098A6078E5CC1895A` identity.
Its `--help`, `generate --help`, and `serve --help` were inspected without loading
models or launching the application. The executable's internal version is 3.3.0.

The pinned upstream [CLI implementation](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/main.rs)
supports one-shot `generate --text ... --voice ... --config ... --output-path -`.
The `-` output is stdout WAV audio, so a disk output file is not required. That
command loads the model and prepares the voice on each process invocation.

The official server retains its prepared `--default-voice` in memory and reuses it
when `/tts` requests omit the voice fields; see the [server implementation](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/server.rs).
The application uses this interface to satisfy voice-state reuse without custom
caching or temporary `.safetensors`:

```text
pocket-tts.exe serve --config <verified-private-local.yaml> --default-voice <reference-audio-path> --host 127.0.0.1 --port 0
POST /tts   application/x-www-form-urlencoded   text=<editable text>
```

The ephemeral loopback address comes from the verified process's bounded startup
diagnostics, followed by the official `/health` check. Requests use a dedicated
HTTP client with no proxy, redirects, cookies, or default credentials. No reference
audio is uploaded: the local runtime reads the already-loaded local reference.
The only form field is text; HTTP WAV responses remain on the local computer.
No browser or server web page is opened. Native logs are drained and discarded,
not displayed or persisted. Ambient Hugging Face credentials/cache overrides are
removed from the child environment, and the config points only to verified private
model assets.

## Voice state and native coordination

The same server session handles subsequent generations while the reference object,
model alias/installation identity, and runtime identity remain usable. Runtime and
registration identity are checked again for every request. Changing the reference
or model, detecting a runtime change, cancellation/failure, leaving Voice Cloning,
or closing the window stops the server. A new session then prepares a fresh voice.
Leaving the page deliberately releases the resident model and leases before Settings
can change models/runtime; no model installation, inventory, or runtime-management
implementation was changed.

Generation retains verified model asset handles and runtime/model reader leases,
and launches through the existing `PocketTtsNativeOperation` job/marker lifecycle.
The model and native CLI compatibility checks run before startup. Operations are
asynchronous, duplicate Generate clicks are blocked, and a per-session request gate
also rejects overlap. Cancellation aborts the request, stops the owned native job
and descendants, waits for cleanup, and invalidates the prepared voice. Failed or
uncertain cleanup retains the existing protective metadata and reference/config
files and blocks conflicting operations rather than guessing that native work ended.

When the native runtime cannot decode an original reference, startup retries once
with the existing Windows-decoded reference WAV fallback. This is the existing
reference-conditioning fallback; it is not generated speech. The runtime still
performs all model preprocessing. No voice profile is created during Generate.

## Generated audio stays in memory

The response is buffered in a `MemoryStream`, capped at 256 MiB of received audio.
Only a successful HTTP response whose body completes can continue to decoding.
Empty, interrupted, oversized, or cancelled responses never replace a preview.
Media Foundation reads the byte buffer through `SHCreateMemStream` and the same
source-reader/float-PCM path used for references. The waveform/peak calculation is
shared too. There is no generated-audio output filename, file write, temp WAV, or
permanent WAV, and no header repair, inference, conditioning, or resampling is
implemented in the application.

The runtime's [streaming WAV writer](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/audio.rs)
uses a nominal billion-frame header for non-seekable outputs. Targeted Windows
checks confirmed that the in-memory decoder uses the actual received sample count,
giving accurate duration without changing the header or audio. The encoded buffer
is released after decoding. The current preview keeps only decoded samples and
waveform data; a successful replacement disposes the old playback source and drops
the old data references. Closing releases both players and preview data. Failed
generation preserves the last successful preview.

## One reusable player

`AudioPlayerControl` is a WinUI 3 UserControl extracted from the former MainWindow
Reference player. Both Reference audio and Voice preview instantiate it. Waveform
rendering, separate gray/played geometry, native Play/Pause, direct waveform pointer
capture and click/drag seeking, frame-coalesced seeks, exact-end restart handling,
smooth playhead movement, times, and resize/DPI behavior have one implementation.
Each instance has its own player, PCM source, position, seek state, geometry, and
render subscriptions. The existing Windows `MediaPlayer`/`MediaStreamSource` backend
remains shared code, with independent instances. Reference borders, spacing, sizes,
and colors were retained. The unused inactive-player template was removed.

An empty player is collapsed in its entirety and reserves no layout space. Successful
reference import or preview generation calls the same control's `SetAudio` method,
which shows it. `Clear` releases its audio/geometry ownership and collapses it.
The page has no empty-player placeholder or preview-unavailable caption. Generation
failure/cancellation does not call `Clear`, so the previous successful preview stays
visible and playable.

Generation progress and Cancel generation appear in Generate preview, above the
separate Voice preview card. That entire card is collapsed until the first valid
generation and stays visible with the previous successful audio on failure. It
contains no export controls; Export voice profile is the last card below Voice
preview in the page's single vertical flow. Success, errors,
cancellation, and playback warnings use the existing notification panel. Playback
and seeking are available after successful generation; playback does not start
automatically. Export's existing naming, confirmation, validation, progress,
cancellation, and publication remain unchanged; its reference-pause call now delegates
to the shared control. The common renderer uses display-only aggregation at two
effective pixels per bar and a three-bar weighted average, retaining exact silent
bars and leaving the decoded PCM/envelope and time-based seeking unchanged.

## Limits and verification

- This verified Rust binary is CPU-only and rejects training `.pt` checkpoints.
- Reuse is process/session-scoped; stopping the server discards its voice/model state.
  Keeping a session ready retains its model memory. There is no cross-session voice cache.
- The preview waits for a complete generated response and decode; it does not play
  partially generated speech. Longer output needs proportional encoded/decoded RAM;
  the 256 MiB response cap reports a clear error for oversized previews.
- Server preparation has a five-minute bound; the local health check has a five-second
  bound. Generation has no fixed duration limit and can be cancelled.
- Windows Media Foundation/installed media components are still required. Windows N
  editions may need the Media Feature Pack.

Initial implementation verification used synthetic memory/temporary reference fixtures
and local CLI diagnostics. A later isolated runtime streaming probe used verified
private `english_2026-10` weights, a local reference, and 302 words. It received the
first nonzero PCM at 81.16 ms, 313 audio chunks, and a complete response at 25.528 s;
Media Foundation decoded 126.36 seconds of audio entirely in memory. This confirmed
that the pinned official HTTP server genuinely streams during generation. The
application still buffers the response to completion; streaming playback is not
implemented by the UI/export completion change. No generated WAV files, model
downloads, application launch, or GUI automation were involved. Actual UserControl
rendering/pointer interaction remains unverified interactively.

Targeted commands:

```powershell
dotnet build .\PocketTTSStudioLite\PocketTTSStudioLite.csproj --configuration Release
dotnet run --project .\tests\PocketTTSStudioLite.ModelSetupChecks\PocketTTSStudioLite.ModelSetupChecks.csproj --configuration Release -- --generation-only
dotnet run --project .\tests\PocketTTSStudioLite.ReferenceAudioChecks\PocketTTSStudioLite.ReferenceAudioChecks.csproj --configuration Release -- --memory-player-only
```

The generation checks cover official server arguments, strict complete loopback
address discovery, form encoding and default-voice reuse, bounded/incomplete/failed/
cancelled response handling, memory Media Foundation decoding of ordinary and native
streaming WAV headers, actual waveform/duration data, and the shared-control layout.
Muted headless Windows player checks exercise two independent instances, playback,
seeking, duration, and completion without audio files or WinUI application startup.

Initial results: application Release build succeeded with zero warnings/errors; all 30
generation/memory checks, 6 independent-player checks, 61 reference filtering checks,
50 selection/persistence checks, 27 notification checks, and 35 existing profile
publication/layout checks passed (209 targeted checks total).

UI/export completion verification reran the generation group with 32 passing checks,
including initial player collapse and placeholder removal, and passed the application
Release build with zero warnings/errors. See [current export verification](voice-export.md#current-completion-verification-and-limits)
for folder persistence, transactional publication, and notification results.
