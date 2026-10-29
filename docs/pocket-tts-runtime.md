# Pocket TTS Rust runtime development validation

Verified on 2026-10-27. The C# integration exposes diagnostics and a native English
voice-export workflow ready for manual testing; the
runtime is not bundled or invoked during application startup. No model weights
were downloaded and no audio was processed.

## Runtime identity and origin

| Field | Verified value |
| --- | --- |
| Official repository | <https://github.com/kyutai-labs/pocket-tts> |
| Latest stable release at verification | [v4.0.1](https://github.com/kyutai-labs/pocket-tts/releases/tag/v4.0.1) |
| Release publication | 2026-10-21 13:14:05 UTC |
| Release ID | `419577605` |
| Release commit | `a922b912dfa366975294afb568d815b305acde5d` |
| Windows target | `x86_64-pc-windows-msvc` |
| Windows asset | `pocket-tts-3.3.0-x86_64-pc-windows-msvc.zip` |
| Asset ID | `662617326` |
| Archive size | 7,914,990 bytes |
| Executable version output | `pocket-tts 3.3.0` |

Download URLs:

- [Windows x64 archive](https://github.com/kyutai-labs/pocket-tts/releases/download/v4.0.1/pocket-tts-3.3.0-x86_64-pc-windows-msvc.zip)
- [Official SHA256SUMS](https://github.com/kyutai-labs/pocket-tts/releases/download/v4.0.1/SHA256SUMS)
- [Release API metadata](https://api.github.com/repos/kyutai-labs/pocket-tts/releases/419577605)

**Version mismatch:** the release tag is `v4.0.1`, while the archive and executable
say `3.3.0`. The release-pinned [Rust manifest](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/Cargo.toml)
also specifies `3.3.0`. Preserve the release tag, target, asset ID, and SHA-256
together; the executable's version alone does not identify this release artifact.

## Integrity

The archive's SHA-256 matched both its entry in Kyutai's `SHA256SUMS` and the
GitHub release asset's `digest`. The downloaded `SHA256SUMS` also matched its own
GitHub asset digest. Asset sizes matched the API metadata before extraction.

| File | SHA-256 |
| --- | --- |
| Downloaded Windows ZIP | `98753041f2e65744b989776f87bccb93f1f574a08afe1890c5427ec94cd6309c` |
| Downloaded SHA256SUMS | `bc5b46085cb70aa0d983195964b098068d7d40f02785b8e3ca6aa4e081add1f2` |
| Extracted pocket-tts.exe | `813cabfc2f8fc7a4e5310deda6025426b3164610dd9f3c3098a6078e5cc1895a` |
| Extracted LICENSE | `36516aefdc84c5d5a1e7485425913a22dbda69eb1930c5e84d6ae4972b5194b9` |

The EXE has no Authenticode signature (`NotSigned`). No detached signature was
listed among the release assets. These checks establish consistency with the
official HTTPS release and its checksums; they are not an independent publisher
signature. GitHub reports this release as mutable (`immutable: false`), so retain
the recorded digests when revalidating it.

## Development files

The dedicated directory is ignored by `/development/runtime/` in `.gitignore`:

```text
development/runtime/pocket-tts/v4.0.1/windows-x64/
  pocket-tts-3.3.0-x86_64-pc-windows-msvc.zip
  SHA256SUMS
  release-api.json
  provenance.json
  extracted/pocket-tts-3.3.0-x86_64-pc-windows-msvc/
    pocket-tts.exe
    LICENSE
  validation/
    results.json
    *.stdout.txt
    *.stderr.txt
    pe-dependencies.txt
    pe-headers.txt
  source-evidence/
    ...release-pinned source files inspected during validation...
```

The official archive contains exactly **`pocket-tts.exe` (22,080,000 bytes)** and
**`LICENSE` (1,046 bytes)**. Keep the MIT license with any redistributed runtime.
There are no companion DLLs or model weights in the archive. Model configs are
embedded in the binary by its [build script](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/build.rs).
Python, PyTorch, Cargo, Rust, CMake, and FFmpeg are not needed to run these CLI checks.

`dumpbin` confirmed an x64 Windows console executable. Its direct DLL imports
are Windows system libraries; no VC runtime DLL imports were listed. The release
[workflow](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/.github/workflows/rust-release.yml)
builds Windows with a static C runtime. Minimum Windows compatibility and model
execution on other machines have not been tested.

## Targeted CLI checks

Each child process had redirected stdout/stderr, no console window, a 15-second
timeout, an isolated `HF_HOME`/`HF_HUB_CACHE`, and `HF_HUB_OFFLINE=1`. Only help,
version, and missing-argument parsing were executed. The offline environment
setting is a precaution, not proof of offline model-loading support.

| Arguments | Observed exit code | Result |
| --- | --- | --- |
| `--version` | 0 | Reports `pocket-tts 3.3.0` |
| `--help` | 0 | Lists `export-voice` |
| `export-voice --help` | 0 | Prints usage and supported options |
| `export-voice` | 2 | Rejects missing input and output arguments on stderr |

All four checks passed. No isolated Hugging Face cache directory was created and
no `.safetensors` files were present in the development directory after validation.
No inference, voice export, model download, authentication, or WinUI regression
tests were run.

To repeat the harmless CLI inspection from the repository root:

```powershell
$runtimeExe = '.\development\runtime\pocket-tts\v4.0.1\windows-x64\extracted\pocket-tts-3.3.0-x86_64-pc-windows-msvc\pocket-tts.exe'
& $runtimeExe --version
& $runtimeExe export-voice --help
```

## Export command contract

```text
pocket-tts.exe export-voice [OPTIONS] <AUDIO_PATH> <EXPORT_PATH>
```

| Argument or option | Meaning |
| --- | --- |
| `AUDIO_PATH` | Reference audio input path |
| `EXPORT_PATH` | Destination for the native Safetensors voice state |
| `-q`, `--quiet` | Suppress logging output |
| `--language <LANGUAGE>` | Choose an embedded model configuration; default `english`, alias of `english_2026-10` |
| `--config <CONFIG>` | YAML configuration supplied by local path, HTTPS URL, or `hf://` path; mutually exclusive with `--language` |
| `-h`, `--help` | Print command help |

The full language list and exact help text are saved locally in
`validation/export-voice-help.stdout.txt`. This command exposes no model-directory,
revision, authentication, offline, device, quantization, or thread-count options.

## Findings for C# integration

- **CPU baseline:** official Windows builds target x86-64-v3, including AVX2/FMA.
  CLI parsing success does not establish compatibility for model computation on
  older x64 CPUs. See the pinned [CPU build configuration](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/.cargo/config.toml).
- **Local model assets:** actual export requires cloning-capable weights and the
  selected config's tokenizer, plus split model/codec files where that config
  specifies them. They are not included in this preparation. Embedded configs may
  trigger downloads. Application-managed export resolves every asset to a
  verified private path and removes the non-cloning fallback; a local `--config`
  alone does not make its asset references local. The default
  English config references pinned `model.safetensors` and `tokenizer.json` assets.
- **Model capability:** the loader can fall back to weights without voice cloning
  if primary weights cannot be obtained. Such weights cannot export an audio
  reference. Availability of a model file must not be treated as proof of cloning
  support. See [model loading](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/model.rs).
- **Authentication and cache:** the [download code](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/download.rs)
  uses Hugging Face client environment/cache configuration, including `HF_TOKEN`
  and `HF_HOME`; it also accepts revision-qualified `hf://` references. There is no
  OAuth flow in this CLI. The application installer owns downloads and OAuth;
  managed export supplies only verified private paths and strips inherited HF
  cache-location settings and tokens. Use local audio paths to keep reference
  audio local. Direct CLI invocations with default/remote configurations retain
  upstream cache behavior and are outside application-managed export.
- **File contract:** help mentions directories, but the inspected export path
  reads one audio input and serializes one Safetensors output; no directory loop
  is present. Directory support is unverified and should not be exposed yet.
  Export processes at most the first 30 seconds of audio. No batch export or
  actual file creation has been tested.
- **Process contract:** use `ProcessStartInfo.ArgumentList`,
  `UseShellExecute=false`, `CreateNoWindow=true`, and asynchronous stdout/stderr
  reads. Handle nonzero exit codes, cancellation, and timeouts. There is no
  structured progress API exposed by `export-voice`; missing arguments return 2,
  while the inspected main function returns 1 for execution errors. Only code 2
  was exercised as a failure case here. Tensor thread count is controlled by
  `RAYON_NUM_THREADS`, with a source default capped at four available CPUs.

The command contract and file behavior were checked against the executable and
the release-pinned [CLI source](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/src/main.rs).
Successful voice export, native profile interoperability, offline model loading,
resource usage, and audio format coverage remain unverified until model and audio
testing is explicitly in scope.

## C# diagnostics integration

`PocketTTSStudioLite.Services.PocketTtsCliService` exposes diagnostic methods
`GetVersionAsync(CancellationToken)` and
`GetExportVoiceHelpAsync(CancellationToken)`. It now also exposes the specific
native command contract through `ExportVoiceAsync` for one local English voice
export; see [voice export](voice-export.md). No arbitrary command runner is public.
Model Setup now runs safe diagnostics automatically, without network access. Their 15-second
deadline is not applied to voice exports or native model retrieval.

Executable resolution uses an explicit constructor path when supplied, otherwise `runtime/pocket-tts/pocket-tts.exe` under `AppContext.BaseDirectory`. Both Debug and Release copy the validated binary and upstream license there during build/publish. The project checks the source executable SHA-256; Model Setup checks it again before diagnostics. There is no environment override or Debug-only ancestor search. Relative explicit paths remain application-relative. See [model setup](model-setup.md).

The service checks Windows/x64 and .NET's `Avx2.IsSupported`/`Fma.IsSupported`
before launch. Missing runtime files produce `FileNotFoundException`, unsupported
systems produce `PlatformNotSupportedException`, and native launch failures
produce `InvalidOperationException` with the original error attached. These are
conservative guards for the validated runtime, not a complete x86-64-v3 or
model-execution compatibility test.

File lookup and process startup run off the calling thread. Execution uses
`ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, `CreateNoWindow=true`,
concurrent asynchronous UTF-8 stdout/stderr reads, and asynchronous exit waiting.
`PocketTtsCliResult` returns the resolved executable path, exit code, stdout,
stderr, and `Succeeded`. A nonzero CLI exit remains available in the result.

Each diagnostic call has a default **15-second timeout**, linked with the caller's
optional cancellation token. The timeout begins when the call is made and covers
startup, execution, and output collection. Timeout/cancellation cleanup is awaited
before the task completes, so cleanup can take additional time beyond the deadline.
Synchronous OS file lookup/startup calls remain on a worker thread and cannot be
interrupted mid-call; cancellation is checked again before/after process startup.

After cleanup, expiration raises `TimeoutException`; explicit caller cancellation
raises `OperationCanceledException` carrying the original caller token. If both
signals are set when the result is reported, caller cancellation takes precedence.
The internal deadline never cancels the caller's token source. No public methods
or constructor signatures changed.

Both interruption paths attempt to kill only the owned process tree and await the
root's exit. Redirected readers are cancellable, observed, and disposed, as are the
process and cancellation sources. Cleanup waits remain bounded to five seconds
each. Cleanup failure takes precedence over a normal timeout/cancellation result:
an `InvalidOperationException` explicitly reports incomplete termination and
preserves the original `AggregateException` details if `Kill(entireProcessTree:
true)` cannot terminate every descendant. The service still attempts to stop and
await the root, but never treats its exit as proof of complete tree termination.
See [Microsoft's process-tree termination contract](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0).

`Process.Kill` alone cannot guarantee termination of descendants that detach before cleanup.
Native invocations now hold private operation reader leases and use durable
named job markers with atomic job-associated creation. Model removal checks
those jobs and conservatively refuses independently running `pocket-tts`
processes. See [model removal](model-removal.md). Normal diagnostics need no
caller-supplied deadline; a caller may still cancel or choose a shorter deadline:

```csharp
var runtime = new PocketTTSStudioLite.Services.PocketTtsCliService();
var version = await runtime.GetVersionAsync(); // Default 15-second timeout.
using var callerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var exportHelp = await runtime.GetExportVoiceHelpAsync(callerDeadline.Token);
```

Initial integration verification on 2026-10-27: **10 checks passed** with a framework-free
console harness linking the actual service source:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.RuntimeChecks\PocketTTSStudioLite.RuntimeChecks.csproj --configuration Debug
```

Historical checks covered real version/help output, app-local and Debug checkout resolution,
relative paths and environment overrides, missing and invalid executables,
pre-cancellation, simultaneous large stdout/stderr with exit code 23, cancellation
of a controlled child tree while preserving an unrelated process, and the CPU
guard in a subprocess with `DOTNET_EnableAVX2=0`. In .NET 10 that switch disables
both AVX2 and FMA; the harness confirms both are unavailable. This simulates the
unsupported-feature branch; a physical legacy CPU and FMA-only absence were not
tested. The fixture is used only to exercise process behavior, not to simulate
successful voice export.

Hardening verification on 2026-10-27: **9 targeted checks passed** (the earlier
full integration suite was not rerun):

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.RuntimeChecks\PocketTTSStudioLite.RuntimeChecks.csproj --configuration Debug -- --hardening
```

These checks cover real runtime version/export-help success, pre-cancellation,
simultaneous stdout/stderr and nonzero exit capture, explicit cancellation with
the original token, the actual 15-second default for each diagnostic command, and
partial tree-termination failures after caller cancellation and after timeout.
The controlled fixture confirms the owned root stops and unrelated processes
remain alive. An internal termination delegate injects `AggregateException`
without changing OS permissions; tests confirm a surviving descendant results in
an incomplete-cleanup error, even when the root has already exited, and then stop
that descendant in fixture cleanup. Real OS access-denied tree failures were not
induced. Public APIs, UI, and model scope are unchanged.

The WinUI project Release build passed with **0 warnings and 0 errors**. No new
dependencies were added. Runtime/model inference and actual voice export remain
unverified and outside this integration's scope.

