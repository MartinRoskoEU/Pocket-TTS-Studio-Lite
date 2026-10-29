# Pocket TTS model discovery

`PocketTtsModelDiscoveryService` reads metadata from the official
`kyutai/pocket-tts` repository using the [Hugging Face Hub model-info API](https://huggingface.co/docs/huggingface_hub/package_reference/hf_api#huggingface_hub.HfApi.model_info).
It uses .NET `HttpClient` and `System.Text.Json`; no Python, scraping, authentication,
model file downloads, CLI execution, or WinUI dependencies are involved.

## Usage and revision handling

```csharp
using var http = new HttpClient(); // Reuse this client for repeated discovery.
var discovery = new PocketTTSStudioLite.Services.PocketTtsModelDiscoveryService(http);
var catalogue = await discovery.DiscoverAsync(cancellationToken: cancellationToken);
var sameRevision = await discovery.DiscoverAsync(catalogue.Revision, cancellationToken);
```

The caller owns the client. Discovery makes one anonymous metadata request:

```text
GET https://huggingface.co/api/models/kyutai/pocket-tts/revision/{revision}?blobs=true
```

The default requested revision is `main`; a branch, tag, or exact commit can also
be supplied. The returned `sha` is the immutable repository commit for the file
inventory in that same response. Every candidate and file record preserves that
commit, rather than relying on the mutable branch name. A request for a full
commit is rejected if the response identifies another commit. Discovery covers
the requested repository snapshot, not all historical commits or tags.

The service requires internet access for each call, including initial discovery.
It has no persistent cache or offline fallback, does not run automatically during
application startup, and never downloads file contents. Its default deadline is
30 seconds (the supplied client's timeout may be shorter). Caller cancellation
preserves the caller token; timeout raises `TimeoutException`. Network/HTTP
failures raise `HttpRequestException`, preserving status codes for access denial,
missing revisions, rate limits, and server errors. Unexpected JSON/schema,
repository identity, paths, hashes, duplicate files, or pagination raise
`InvalidDataException`. JSON responses are limited to 8 MiB. Failed discovery
does not return a partial catalogue.

## Model and file records

Candidates are discovered from the repository layout, not a version allowlist:

- Each `languages/<variant>/` containing direct `.safetensors` files is a candidate.
- Each root-level `.safetensors` file is a separate legacy candidate.
- Nested `embeddings/` files are retained as file metadata and are not treated as
  model variants. They do not implement a voice library feature.

IDs are `kyutai/pocket-tts:<repository directory>` for language candidates and
`kyutai/pocket-tts:<weights filename>` for root candidates. Root candidates have an
empty repository-directory path and an explicit candidate weights path. Identity
and revision are separate fields; use both for reproducibility.

`PocketTtsModelDiscoveryResult` retains the requested and resolved revisions,
retrieval time, gating metadata, full repository file inventory, and candidates.
Each candidate exposes its ID, name, repository, directory, candidate weights
paths, and associated file records. `PocketTtsModelFile` records its path,
revision, available size, Git blob ID, and LFS SHA-256/size/pointer-size metadata.
Missing or redacted hashes remain `null`. A Git blob ID is not an LFS content
SHA-256. Metadata retrieval does not verify downloaded file integrity.

Discovery is deliberately separate from compatibility verification:

- `RuntimeCompatibility` is always `Unknown` for discovered candidates.
- `VoiceCloningSupport` is always `Unknown`; no positive or negative claim is
  inferred from names, file extensions, gating, or model sizes.
- The enums reserve distinct verified/unsupported states for future verification;
  discovery itself cannot produce those verdicts.

New variants under the recognized layout appear without code changes. New
storage formats/layouts require discovery-rule review; all returned file records
remain available even when they are not classified as model candidates. An
inventory with no recognizable candidates fails explicitly.

## Official API verification — 2026-10-27

Resolved commit: **`db04f0e653473e5c7333c69686ac92b7460b0c1d`**.
The [pinned API request](https://huggingface.co/api/models/kyutai/pocket-tts/revision/db04f0e653473e5c7333c69686ac92b7460b0c1d?blobs=true)
returned **28 candidates**, **853 file records**, and gating value **`auto`**.
All 853 records had sizes and Git blob IDs; all 822 LFS records had SHA-256
metadata. Repeating discovery at that exact commit returned identical file
metadata and candidate IDs. These are observations of this revision, not constants
in the service or expectations for future revisions.

The following language candidates each have their weights at
`languages/<name>/model.safetensors`:

| Candidate | Weights size (bytes) |
| --- | ---: |
| dutch | 219029196 |
| dutch_24l | 672178676 |
| english | 219029196 |
| english_18m_2026-10 | 111728528 |
| english_18m_refl_2026-10 | 111728528 |
| english_2026-01 | 235738732 |
| english_2026-04 | 219029196 |
| english_2026-04_24l | 1304206614 |
| english_2026-09 | 219029196 |
| english_2026-09_2 | 219029196 |
| english_2026-09_24l | 1304206614 |
| english_2026-10 | 219029196 |
| english_2m_2026-10 | 46713080 |
| english_2m_refl_2026-10 | 46713080 |
| english_7m_2026-10 | 66482920 |
| english_7m_refl_2026-10 | 66482920 |
| english_drifting_26-09 | 217448348 |
| french | 219029196 |
| french_24l | 672178676 |
| german | 219029196 |
| german_24l | 672178676 |
| italian | 219029196 |
| italian_24l | 672178676 |
| portuguese | 219029196 |
| portuguese_24l | 672178676 |
| spanish | 219029196 |
| spanish_24l | 672178676 |

The legacy candidate **`tts_b6369a24`** is at root path
`tts_b6369a24.safetensors` (235738732 bytes). Aliases or duplicate content are kept
as distinct path identities: for example, `english` and `english_2026-10` have
identical weights hashes at this revision. A separate legacy path shares its
weights hash with `english_2026-01`.

## Compatibility limits and verification

The new [Model Setup service](model-setup.md) separately verifies exact runtime-pinned assets already in the native cache. Discovery results themselves remain Unknown and cannot activate models.

**None of these 28 candidates has been verified compatible with our Pocket TTS
4.x release artifact or verified to support voice cloning by this feature.**
The runtime is the `v4.0.1` Windows artifact that reports `3.3.0`, as recorded in
[runtime validation](pocket-tts-runtime.md). Compatibility needs evidence about
the exact runtime, config, tokenizer, weights, and their revisions; matching a
model name or seeing Safetensors metadata is insufficient. Runtime configs can
also reference assets from another repository or older revisions.

The repository is gated: public metadata discovery succeeding does not establish
authorization to retrieve weights. File contents, tensor schemas, cloning
encoders, complete model asset sets, and actual exports were not inspected or
tested. The application now has a separate [OAuth sign-in service](hugging-face-authentication.md);
discovery still makes anonymous metadata requests, and file downloads remain future work. Discovery does not
call `PocketTtsCliService` or promote models to selectable/compatible states.

Targeted verification: **9 check groups passed**, including live discovery and
exact-commit replay, synthetic future variant names, capability uncertainty,
missing/redacted metadata, connectivity/HTTP errors, malformed/schema-invalid
responses, pagination/size limits, cancellation, and simulated HTTP timeout.
HTTP request guards allowed only anonymous model-info metadata calls. No existing
runtime regression suite was run. The WinUI Release build passed with 0 warnings
and 0 errors.

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ModelDiscoveryChecks\PocketTTSStudioLite.ModelDiscoveryChecks.csproj --configuration Release -- --live
```

Without `--live`, the check program uses only in-memory HTTP fixtures. Live checks
write a timestamped metadata snapshot under Git-ignored `artifacts/model-discovery/`;
the application service writes no files. The snapshot includes exact revisions
and available per-file metadata, not model contents. The real 30-second deadline
and physical network interruption were not forced; those failure paths were
verified with controlled HTTP fixtures.

