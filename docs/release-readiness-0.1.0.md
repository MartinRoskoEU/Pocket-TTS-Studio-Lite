# Pre-release review for 0.1.0

## Repository directory review, 2026-10-29

Inspected the root, application/test output directories, `artifacts/`,
`development/`, installer inputs, and `release/`. **No application source or
functionality was changed. No files were deleted:** automatic approval review
rejected the reviewed cleanup and a narrower obsolete-installer deletion with
`blocked by policy`, without a more specific reason. The removal plan is pending,
not completed.

The reviewed plan covers 47 directories containing 6,004 files (3,215,599,126 bytes,
about 2.99 GiB). Its exact resolved paths, counts and sizes are recorded in ignored
`artifacts/release-review-20261029/cleanup-removal-plan.json`; protected-input hashes
and execution status are adjacent. It identifies these reproducible outputs:

- Old UI publish trees: `artifacts/model-setup-ui`, `model-setup-ui-final`,
  `voice-export-ui`, `unpackaged`, and the `app` / `final-app` children of
  `final-visual-layout`, `precise-redesign`, and `shell-review` where present.
- Earlier `artifacts/installer/build`, `payload`, and `output` trees; failed
  `invalid-runtime-fixture`; and the `payload` / `support` children of old GUID
  staging directories. Historical `native-imports.csv` records are excluded from
  deletion. Installer build/publish paths are live generated destinations, not
  obsolete application references; the existing build process recreates them.
- Application and existing test projects' `bin` / `obj` directories, and the
  streaming probe's `bin` / `obj` directories. The inspection found no audio/model
  files or required source fixtures in those generated directories.
- Empty `artifacts/model-setup-checks` and
  `PocketTTSStudioLite/RuntimeAudioContracts` directories. No active source,
  configuration, or documentation references the latter.

Retain these inputs and evidence:

| Location | Reason retained |
| --- | --- |
| `development/runtime/pocket-tts/v4.0.1/windows-x64` | Required pinned official executable/license, original archive/checksums, release provenance, source evidence and CLI/PE validation. The executable still matches production's validated SHA256. |
| `installer/`, application `RuntimeMetadata/`, assets and configuration, all tests/docs | Required source-release, packaging, pinned configuration and licensing inputs. This includes the MPL source archives, not model weights. |
| `artifacts/installer/tools` | Verified official portable Inno compiler and its download; required by the current local installer build command. |
| `artifacts/installer/*.py` and `license-audit-cache` | Existing license collection/provenance tooling and cached source notices; useful for maintaining the redistribution bundle. Python is not an application/installer-build dependency. |
| Streaming probe source/project, measurements and chunk CSVs | Unique evidence for the confirmed runtime streaming capability; generated probe build output is separately identified above. |
| Runtime export review source archive/extraction and model-discovery snapshot | Useful upstream/API investigation evidence; not assumed obsolete merely because ignored. |
| Review `before/` snapshots, hash manifests, and historical native-import CSVs | Unique comparison/audit evidence. Paths inside historical snapshots remain historical records, not active build inputs. Their future value is uncertain, so they are preserved. |
| `release/PocketTTSStudioLite-0.1.0-win-x64-Setup.exe` | Final installer; excluded from cleanup. Verification rebuilds it through the existing process. |

`.gitignore` was reviewed and needs no rule changes: standard .NET output patterns,
`artifacts/`, `/development/runtime/`, `[Rr]elease/`, `.safetensors`, and existing
credential/output rules remain valid. No source/license archive/test fixture was
found accidentally ignored outside generated output. The remaining optional build
and credential patterns are intentional safeguards, not obsolete directory inputs.
No staging, commits, tags, pushes, private AppData changes, or shared-cache changes
were performed. Application/source directories remain untracked as recorded by the
earlier review; this task does not stage them.

Verification after the directory review: the application Release build succeeded
with **zero warnings and zero errors**. The unchanged installer build process also
succeeded and regenerated `release/PocketTTSStudioLite-0.1.0-win-x64-Setup.exe`
with product version `0.1.0`. The protected-file audit covers 513 unchanged files.
Logs are retained alongside the removal plan. Verification produced normal fresh
build/staging output; it did not execute the blocked removal plan. No tests, app GUI,
actual installation or uninstallation were run for this directory-only review.

## Installer and versioning follow-up

The historical review below describes the state before installer implementation.
Subsequent authorized work establishes `Directory.Build.props` version 0.1.0,
stamps intermediate application/package manifests, aligns the title bar and HTTP
User-Agent, and prepares an x64 per-user Inno Setup distribution with both .NET and
Windows App SDK self-contained. The verified official runtime and required notices
are mandatory packaging inputs. The stable Inno AppId preserves one upgrade/uninstall
identity; private data remains outside application files. Optional confirmed
uninstall cleanup is limited to the fixed, guarded private root.

See [Windows installer documentation](windows-installer.md) for build instructions,
dependency/license review, prerequisites, upgrade/data retention, and remaining
signing/clean-VM distribution checks. No real installation/uninstallation, app GUI
launch, model download, Git commit, tag, or release is performed by this work.

The earlier VoiceExportChecks baseline errors were separately migrated to the
current private-model/prepared-reference APIs before installer work. The historical
baseline result below is retained as a record of that earlier review, not the
current state of the test project.

Reviewed 2026-10-29. This is a source/repository review and cleanup, not installer
implementation or release certification. Application behavior, version values,
dependencies, private user data, and the MIT license are preserved. No commits,
tags, pushes, model downloads, authentication, or application GUI launch were made.

## Resolved

- Removed the permanently collapsed Back to Settings button and its otherwise
  unused handler/control updates. Sidebar navigation already owns this action.
  Removed the permanently collapsed model-access hyperlink; export never showed
  it and installation owns access/authentication guidance.
- Removed the shared player's never-set invalid-binding flag and duplicate closing
  predicates. The page retains its real runtime-binding checks, and `Clear` still
  releases the player. Playback, pointer seeking, waveform display, and visibility
  handling are unchanged.
- Removed the unused model `DisplayName` property and obsolete directory/profile-name
  converter branches. Current bindings use the model's `Name`, separate status
  presentation, the reference filename, and path visibility.
- Removed the unused `ExportVoiceWithModelAsync` entry point and its unreachable
  null-reference export path. The page already uses `ExportPreparedReferenceAsync`;
  its runtime/model checks, WAV fallback, staging, overwrite confirmation, final
  publication, cancellation, and notifications remain in place.
- Consolidated the identical export/generation child-process environment rules in
  `PocketTtsCliService.ConfigurePrivateModelEnvironment`. Both remove ambient
  Hugging Face credentials/cache selectors, preserve an existing HOME, initialize
  an absent/blank HOME, and disable implicit tokens. Diagnostics retain their
  existing environment behavior. Added two checks to the existing generation group.
- Simplified one reference cleanup-message expression and removed a duplicate,
  already-completed archive-hash syntax check in runtime receipt validation. The
  original hash/receipt/path verification is retained.
- Removed 15 duplicate asset Content items. WinUI already includes those images
  and icons; the project now updates only AppIcon's required copy metadata. MSBuild
  evaluation confirmed the same unique content files, no duplicate items, and
  unchanged icon output/publish behavior. No assets were deleted.
- Updated existing layout assertions for the current single-column Reference,
  Generate, Preview, Export order. The former assertions referenced containers
  removed by the prior layout change. No test coverage was deleted.
- Corrected RuntimeChecks to target `net10.0-windows`, consistent with its linked
  Windows registry/COM services. Its seven baseline CA1416 warnings are resolved
  without suppression, analyzer changes, or deletion of checks.
- Updated stale two-column documentation, the obsolete export entry-point name,
  and third-party notices that referenced a nonexistent `LICNESE` file and template
  artwork. The actual `LICENSE` text is unchanged.

## Code retained deliberately

Runtime/model identity checks at activation, operation startup, and publication
protect different boundaries; they are not interchangeable duplicate validation.
Native job ownership, reader/writer leases, durable cleanup markers, link/path/hash
guards, and failed-cleanup retention remain unchanged.

Single-reference scans alone do not establish dead code. For example, success/error
indicator styles are looked up dynamically by `MainWindow.RuntimeUpdates.cs`, and
unread fields in native structs preserve Windows ABI layout. Those were retained.
Both Windows-qualified copies of the identical 48-pixel icon are valid resource
variants and were retained. Tested discovery/authentication APIs were also retained.

Further consolidation of JSON settings, async operation guards, or lifecycle state
would cross distinct failure/cancellation policies. It is not warranted for this
behavior-preserving cleanup. No generic framework or new dependency was added.

## Repository audit

At review time Git tracks only `.gitignore`, `AGENTS.md`, `CHANGELOG.md`, `LICENSE`,
`README.md`, and `THIRD_PARTY_NOTICES.md`. The application, docs, and tests directories
remain untracked. Review and include the intended source/resources before releasing;
this review deliberately did not stage or commit them.

The current tracked and non-ignored untracked files contain no model weights,
downloaded executable/archive, generated audio, certificate/private key, or
credential-shaped token found by the source scan. This is a current-tree check,
not a guarantee about Git history or arbitrary external files. The OAuth client ID
is a public registration value, not a client secret.

The 25 YAML configurations, 12 repository JSON snapshots, upstream license, and
application artwork are required source resources and remain. Their metadata is
used for pinned installation plans; it is not a model installation inventory.
`artifacts/`, `bin/`, `obj/`, `development/runtime/`, model/profile output, and
credential/certificate patterns are ignored. Existing screenshots, measurements,
streaming probe, and other diagnostic artifacts were preserved. No user data,
private model storage, or shared Hugging Face cache was removed or modified.

## Remaining release concerns

1. **Legacy export checks are a confirmed baseline build failure.**
   `tests/PocketTTSStudioLite.VoiceExportChecks` fails with two CS0246 errors because
   its selected source links omit `PreparedReferenceAudio`. Its Program also uses
   the removed older `ExportVoiceAsync`/`--language`/shared-cache contract. Merely
   adding the missing links would not migrate those fixtures. Preserve its useful
   cancellation, long-export, error-classification, and collision checks and migrate
   them to the current verified private-model/prepared-reference API in a separate
   test-maintenance task; do not resurrect the old production API to make it compile.
   This project was not deleted or claimed to pass.
2. **Default diagnostic entry points are not isolated CI checks.** The no-argument
   ModelSetupChecks path uses the development runtime/live local setup and older
   asset assumptions. RuntimeChecks' default path assumes app-local runtime resolution.
   They were not executed. The named ModelSetupChecks modes below are the isolated
   checks verified here. Review/migrate the defaults before treating all projects as
   a single automated release gate.
3. **Packaging structure exists, but distribution needs decisions and validation.**
   The WinUI application, package manifest, artwork, x64 publish profile, and
   app-relative native runtime layout are suitable inputs to an installer. The
   evaluated x64 publish profile has .NET `SelfContained=true`, MSIX packaging, and
   no explicit `WindowsAppSDKSelfContained` value. .NET self-contained publishing
   does not itself select self-contained Windows App SDK deployment. Choose
   MSIX versus another installer and the framework-dependent/self-contained model,
   provision dependencies accordingly, and validate on a clean Windows machine.
   See Microsoft's [deployment overview](https://learn.microsoft.com/windows/apps/package-and-deploy/deploy-overview)
   and [self-contained deployment guide](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps).
4. **Signing, identity, and version are not release-finalized.** Package identity
   remains the existing GUID, publisher `CN=Nendis`, and manifest version `1.0.0.0`;
   the project evaluates Version `1.0.0`. No signing credential is committed or
   explicitly selected in project settings. Resolve identity/version/signing as
   part of packaging, without committing a private signing key. MSIX must be signed
   and trusted for installation; see [Microsoft's signing guidance](https://learn.microsoft.com/windows/msix/package/signing-package-overview).
   HTTP user agents also contain both `0.1.0` and `1.0`; reconcile them with the
   chosen release version during the authorized versioning step. No values were
   changed in this review.
5. **Architecture profiles exceed runtime support.** The template advertises x86
   and ARM64 as well as x64, but `EnsureSupportedProcessor` requires Windows x64,
   an x64 process/OS, AVX2, and FMA. Prepare an x64 release and document/enforce those
   prerequisites; do not advertise the other profiles as working native runtimes.
   Existing architecture definitions were not changed.
6. **Native payload and notices need explicit package gates.** The verified runtime
   source exists on this machine, but is intentionally Git-ignored. Its project
   Content/hash-check entries are conditional on the source executable existing,
   so a fresh build without it can succeed while omitting the fallback runtime.
   Release packaging must require the pinned executable and adjacent upstream
   LICENSE. The root application LICENSE and THIRD_PARTY_NOTICES are not currently
   marked as publish Content; include the required notices in the distribution and
   review resolved NuGet notices. No installer or redistribution bundle was built.
7. **Interactive and real-operation validation remains separate.** This review did
   not exercise actual OAuth/model downloads, native profile export, GUI resizing,
   file/folder dialogs, or installed-package launch/uninstall. The successful prior
   streaming probe is useful runtime evidence, not a replacement for those checks.
   One symlink fixture was skipped because Windows Developer Mode/privilege is absent.

## Verification

Application Release build: zero warnings and zero errors. RuntimeChecks Release
build after the Windows TFM correction: zero warnings and zero errors.

| Existing check mode/project | Result |
| --- | --- |
| ModelSetupChecks `--generation-only` | 34 passed |
| ModelSetupChecks `--profile-export-only` | 44 publication/layout + 33 folder checks passed |
| ModelSetupChecks `--selection-only` | 21 selection + 29 persistence checks passed |
| ModelSetupChecks `--notifications-only` | 27 passed |
| ModelSetupChecks `--waveform-only` | 16 passed |
| ReferenceAudioChecks `--filtering-only` | 61 passed |
| ReferenceAudioChecks `--memory-player-only` | 6 passed |
| ModelRemovalChecks | 17 passed; 1 symlink check skipped |
| ModelDiscoveryChecks, without `--live` | 8 mocked check groups passed |
| AuthenticationChecks, without `--live-device` | 8 mocked check groups passed |
| Legacy VoiceExportChecks build | Baseline failure described above; not repaired or executed |

Total: 288 individual checks and 16 mocked scenario groups passed, with one skipped
fixture. Existing test projects/frameworks were used; no new test infrastructure
was introduced. Only synthetic temporary files, in-memory audio, mocked HTTP, and
owned fixture native processes were used. The application GUI was not launched.
