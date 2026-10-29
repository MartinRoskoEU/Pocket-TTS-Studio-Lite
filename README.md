# Pocket TTS Studio lite

Pocket TTS Studio lite is a free, open-source desktop application for creating custom voice profiles using Kyutai Pocket TTS.

The application provides a simple graphical interface for voice cloning without requiring users to work with command-line tools.

Voice profiles are exported in the native `.safetensors` format.

## Project Status

**In development — Version 0.1.0**

The initial release will focus on voice cloning and model management.

The application checks the official runtime and registered private model files locally at startup. It restores the last explicitly selected model only when installed, verified, and compatible, then opens Voice Cloning. With no previous selection state, it activates the first usable model. A removed or unavailable selection stays empty without choosing a replacement; Settings opens when no model is activated. Automatic activation failures return to Settings with the existing setup error status. Settings supports explicit, cancellable installation from trusted pinned metadata in the official `kyutai/pocket-tts` repository, with the existing OAuth flow when required. See [model setup](docs/model-setup.md) and [model installation](docs/model-installation.md).

The Settings page contains full-width **Pocket TTS Runtime** and **Models** cards, using theme-aware Fluent brushes, thin borders, 8-pixel corners, and 20-pixel padding. The Models card lists active-runtime aliases together with reliably identified local models, deduplicated by model identifier. Rows show Installed, Not installed, Unverified, or Incompatible, with Install enabled only for supported models with complete trusted installation metadata; Remove is enabled for all registered models, including Unverified and Incompatible entries, with native confirmation. **Check for updates** first refreshes local information, then anonymously compares only registered installed models against current official main metadata. Separate update labels show Checking for updates..., Up to date, Update available, or Unable to check, based on complete payload SHA-256/Git blob SHA-1 and size comparisons. After local startup initialization/navigation, anonymous background checks run for registered installed identities not successfully checked within 24 hours. Newly installed identities remain eligible independently. Successful UTC timestamps and identity-bound results persist in `%LOCALAPPDATA%\PocketTTSStudioLite\model-update-checks.json`; incomplete/failed checks do not advance them. Cached results require the same locally verified installation identity. Manual checks always bypass the interval; shared guards prevent duplicate checks. Background failures are silent and checks do not block startup, navigation, export, or model installation. Checks do not download files, request sign-in, or alter inventory. See [model update checks](docs/model-update-checks.md). There is no model selection, details panel, Use Model action, online discovery control, or nested list scrollbar in Settings. Model identifiers and statuses wrap into a responsive layout on narrow windows.

While installing, a dedicated Fluent card between Runtime and Models shows the
model identifier, real operation/download progress, and Cancel installation.
The same card also reports confirmed model removal, partial cleanup/recovery, and a dismissible completion, failure, or cancellation result,
retained across page changes. It is brought into view on installation start or
returning to Settings; the Models list has no installation-status footer. The
repository conditions link stays in the authentication dialog.

Persistent installation records at
`%LOCALAPPDATA%\PocketTTSStudioLite\model-inventory.json` retain model aliases,
repository commits, required file hashes, canonical private blob references, and
mandatory native configurations independently of the active runtime. Refresh combines these
records with runtime aliases, without
duplicate identifiers. Recorded files are always reverified: missing/changed files
show Unverified; verified installations absent from a successfully detected runtime
alias list show Incompatible. Failed runtime detection leaves compatibility unknown.
Only supported models passing all existing voice-cloning checks can be activated.
Unregistered private blobs and shared HF cache contents do not establish installation.
The inventory is created only after real registration; no entries are generated
from runtime aliases or online searches. Installation registers only fully verified files; installation of model updates remains unimplemented. [Model removal](docs/model-removal.md) commits unregistration and pending cleanup before deleting unreferenced private blobs, preserves shared assets, deactivates removed models, and refreshes in place. Install never activates a model or leaves Settings automatically.

Models are managed exclusively in `%LOCALAPPDATA%\PocketTTSStudioLite\models`,
with verified content-addressed `blobs/sha256/` and `blobs/git-blob-sha1/` files,
private operation staging, and no external symlinks/hardlinks. Inventory schema 2
tracks each model's references, deduplicating common dependencies. The application
never searches, copies from, or writes model assets to a shared Hugging Face cache.
Installation verifies existing private blobs and downloads missing pinned files
directly into private staging, independently of global HF cache settings.
Publication and registration coordinate with verification/export through private
reader/writer leases. Export uses an explicit verified local configuration with
no remote references or non-cloning fallback. Existing schema-2 inventories and
private files are retained; only unsupported old inventories require manual
archiving before reinstalling. Shared-cache cleanup is separate from the app. See
[private storage and exact development cleanup](docs/private-model-storage.md).

The native Mica window opens centered in the monitor work area at 1180 by 780 effective pixels (limited to the work area on smaller displays). A 40-pixel integrated title bar preserves native Windows caption buttons and dragging. The interface follows the Windows light/dark theme, uses Segoe UI Variable, and has 28-pixel content padding with 20-pixel section spacing. The left navigation pane stays expanded at 208 pixels, lists **Voice Cloning** before **Settings**, and has no collapse button. Settings is selected while startup verification runs. Each view retains its controls and state when navigating. Voice Cloning is accessible before activation, with its existing model-dependent controls disabled. Navigation away from an active export or file picker remains disabled.

The application icon uses the provided Pocket TTS Studio Lite PNG artwork.
`Assets/AppIcon.ico` contains transparent 16, 24, 32, 48, 64, 128, and 256-pixel
frames and is embedded in the executable. `AppWindow.SetIcon` loads the staged
ICO for the main window and taskbar. Matching PNG assets supply the custom
title-bar image and packaged Windows logos, preserving the original artwork's
colors, transparency, and proportions.

Voice Cloning uses one full-width vertical flow: notifications under the heading, Reference audio, Generate preview, Voice preview, then Export voice profile. Cards size to their content with consistent Fluent padding and 20-pixel gaps. The page description is “Clone a voice from reference audio, preview generated speech, and export a reusable voice profile.”

Reference audio retains its native picker/drop filtering, decoded waveform, playback, seeking, and times. Its drop area stays visible for replacing audio; the filename and player appear only when loaded. Import/playback remain independent of model/runtime availability. Both players use the same display-only peak aggregation and light smoothing for clearer waveform bars, without changing PCM, timing, or playback. See [reference audio and waveform display](docs/reference-audio.md).

Generate preview contains the persistent Model ComboBox, the editable Preview text field, Generate, and generation progress/cancellation. With a usable model/reference and nonempty text, Generate invokes the verified local Rust server and reuses its prepared default voice. Generated audio stays in memory and uses the same shared player. The entire Voice preview card appears only after successful generation and retains its last successful audio on failure. Export controls live in Export voice profile and use the existing native export workflow independently of Generate. See [generation and memory handling](docs/voice-generation.md) and [page layout and export](docs/voice-export.md).

Both shared audio players are completely hidden until their audio is available; there are no empty-waveform placeholders. The reference drop zone remains visible. Clearing a player collapses it without reserving space, and a failed generation retains the previous successful preview.

The folder button opens the native folder picker without a Save As step and is available without an active model. The selected folder is saved immediately using the existing local JSON settings convention (`%LOCALAPPDATA%\PocketTTSStudioLite\voice-export.json`) and restored at startup. Missing or inaccessible destinations report a notification and keep Export disabled until another usable folder is chosen. Folder access is checked again before export.

Export generates `<reference-audio-filename>_<model-alias>.safetensors`, removing the audio extension and safely handling Windows filename restrictions. It uses the loaded reference and verified selected model through the official runtime's native `export-voice` command; Generate and preview audio are not prerequisites. There is no version suffix. If that filename already exists, a native WinUI dialog shows it and offers Overwrite or Cancel (the default). Cancel, export failure, or cancellation leaves the original intact. A confirmed replacement is published atomically only after export and cleanup succeed. Concurrent unconfirmed destinations are never overwritten. See [automatic filenames and overwrite confirmation](docs/voice-export.md#automatic-filenames-and-overwrite-confirmation).

## Development

Release **0.1.0** uses a per-user Windows x64 Inno Setup installer, with .NET and
Windows App SDK included. The installer bundles the verified official Rust runtime
and licenses, but no model weights. See [Windows installation, upgrades, prerequisites,
uninstallation, and building Setup](docs/windows-installer.md). The setup EXE is
currently unsigned; clean-machine installation/upgrade/uninstall validation remains
required before public distribution.

`Directory.Build.props` owns the release version. Assembly/package metadata, the
displayed application version, HTTP User-Agent, and installer derive from that value.
The [0.1.0 pre-release review](docs/release-readiness-0.1.0.md) records the preceding
cleanup and its historical validation results; installer follow-up is recorded there.
It also records the final directory inventory, retained runtime/build inputs and
diagnostics, and the pending artifact-removal plan that automatic approval review
blocked. No directory cleanup is claimed complete.

The application project is `PocketTTSStudioLite/PocketTTSStudioLite.csproj`, with the
`PocketTTSStudioLite` namespace. It targets .NET 10 and uses the official Windows App SDK.

Prerequisites:

- Windows with the .NET 10 SDK and its desktop runtime.
- Windows App Runtime 2.5 for the machine architecture.
- Developer Mode enabled in Windows Settings for the default packaged launch.
- Internet access for the initial NuGet restore.

The project was generated with Microsoft's official `winui` blank template from
`Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` version `0.0.7-alpha`, the current
official CLI template pack at initialization. The runtime dependencies use stable,
explicit versions: Windows App SDK `2.5.1`, Windows SDK BuildTools
`10.0.28000.2705`, and BuildTools.WinApp `0.7.1`.
BuildTools.WinApp supplies the CLI used by `dotnet run`; a separate global
`winapp` installation or Visual Studio installation is not required for this workflow.
See the [official template documentation](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/README.md).

From the repository root in the VS Code PowerShell terminal:

```powershell
dotnet build .\PocketTTSStudioLite\PocketTTSStudioLite.csproj --configuration Release
dotnet run --project .\PocketTTSStudioLite\PocketTTSStudioLite.csproj --configuration Release
```

`dotnet run` builds, registers a development package, and opens the application.
If it reports that Developer Mode is disabled, enable Developer Mode in Windows
Settings before retrying. No application code changes system settings.

To run the same application unpackaged without enabling Developer Mode:

```powershell
dotnet run --project .\PocketTTSStudioLite\PocketTTSStudioLite.csproj --configuration Release --no-launch-profile -p:WindowsPackageType=None -p:EnableWinAppRunSupport=false -p:OutputPath=..\artifacts\unpackaged\
```

This command uses a separate ignored output directory and leaves the project's
default packaged configuration unchanged. Release trimming and ReadyToRun are
disabled so ordinary framework-dependent CLI builds work without a self-contained
publish configuration.

## Development runtime validation

The official Kyutai Windows x64 Rust runtime from release `v4.0.1` has been
downloaded into the Git-ignored `development/runtime/` directory and checked
against the official checksums. Its executable reports `pocket-tts 3.3.0`.
Targeted CLI checks passed, including `export-voice --help`; no models were
downloaded and no voice cloning was performed.

`PocketTtsCliService` provides asynchronous version and export-help diagnostics,
with stdout/stderr and exit-code capture, cancellation, and CPU/path validation.
Both commands have a default 15-second timeout (`TimeoutException`); explicit
caller cancellation preserves `OperationCanceledException` and its original token.
Cleanup is awaited on either path. Partial process-tree termination raises an
incomplete-cleanup error with the original aggregate failure attached.
Debug and Release stage the validated executable and license at `runtime/pocket-tts/` beside the app. Startup verifies runtime identity and registered private assets offline. No environment override is needed. See [model setup](docs/model-setup.md) for source directory configuration and missing-runtime errors.

The bundled `v4.0.1` runtime remains an unchanged fallback, verified against its
trusted executable SHA-256. The active release and executable SHA-256 are saved
in `%LOCALAPPDATA%\PocketTTSStudioLite\runtime-identity.json`. Verified updates
live in version/hash-specific local directories, with installation receipts.
Later checks rehash the selected runtime against its identity and receipt (or
the trusted bundled pin). Release identification never uses `--version` output;
the existing version diagnostic remains separate.

After the startup model check, a background runtime update check verifies the
saved identity and requests only the official GitHub `releases/latest` metadata,
with a 10-second network timeout. It compares `tag_name` with the saved release tag
using semantic version precedence and identifies the Windows x64 Rust ZIP asset.
Offline/API failures leave startup and local export available. The structured
result is retained in `MainWindow.RuntimeUpdateCheck` and displayed on the Settings
runtime card. **Check for updates** reruns the same service asynchronously and is
disabled during the check. A newer release with a compatible asset changes the
button to **Update**. It opens a native confirmation showing installed and new
release tags. Only confirmation starts the runtime download, with progress and
cancellation in the card. Installation requires GitHub's official ZIP SHA-256
digest, safe extraction, Windows x64/platform checks, and compatible local-export
CLI diagnostics. The verified runtime is then activated with an atomic identity
switch and a previous-identity backup; failures retain or restore the old runtime.
Previous runtimes and the bundled executable are never overwritten or deleted.
Startup never installs updates, and model files/authentication are unchanged.
See [runtime update checks and installation](docs/runtime-update-checks.md).

Historical diagnostic checks (not rerun for the model-first change):

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.RuntimeChecks\PocketTTSStudioLite.RuntimeChecks.csproj --configuration Debug
```

For only the timeout/cancellation hardening checks, append `-- --hardening`.

See [runtime validation](docs/pocket-tts-runtime.md) for exact versions, URLs,
hashes, required files, command arguments, and integration constraints.

## Voice Cloning

Startup restores the last explicit Model ComboBox selection when it is still installed, verified, and runtime-compatible. Only when no previous preference exists does it automatically activate the first usable model. The preference is stored separately from inventory in `%LOCALAPPDATA%\PocketTTSStudioLite\model-selection.json`. Removing the selected model saves an empty selection, requiring an explicit replacement even after restart. An unavailable saved alias never triggers fallback. Settings manages model inventory without selecting the export model. Voice Cloning uses the existing activation service and checked Settings rows. Refresh preserves the selected alias only while it remains verified and runtime-compatible.
Then choose local audio and an export folder, and use Export in Export voice profile. Local-only native
configs preserve offline operation and prevent automatic model downloads.
See [voice export](docs/voice-export.md) for cancellation and manual-test limits.

The model setup/selection checks target `net10.0-windows`, matching their linked
Windows registry and Media Foundation services. The targeted `--selection-only`
command and verification results are documented in [voice export](docs/voice-export.md#model-selection).

Reference audio browsing offers only the centrally discovered supported extensions,
with no All files option. DragOver and Drop validate the same list and accept only
one file; unsupported extensions, folders, and other drag contents are rejected
before decoding. Rejected drags use the native Not allowed indicator and preserve
the loaded reference. Windows retains the original dragged file icon or thumbnail
and supplies its standard Copy or prohibited feedback; the app adds no drag-image,
caption, badge, or cursor overrides. Existing decoder validation is retained.
See [reference audio](docs/reference-audio.md)
for the targeted filtering checks and verification limits.

Voice Cloning reports import/decoding errors, playback warnings, model-selection
errors, and voice-profile export results in one full-width dismissible Fluent
notification below its heading. It shares Settings' styling and supports success,
error, warning, and information headings/icons. New events replace the previous
notification; inactive panels are hidden. Export progress stays beside its controls,
without duplicate inline outcome messages. See [notification behavior and checks](docs/voice-export.md#voice-cloning-notifications).

## Hugging Face sign-in

Authentication belongs exclusively to model installation. Each Install attempt
starts anonymously; if authorization is required, the existing OAuth Device Flow
opens automatically in a native installation dialog. The dialog links directly
to `kyutai/pocket-tts` and explains that repository conditions must be reviewed
and accepted separately on Hugging Face when required. There is no persistent
authentication section or Sign in/Sign out control in Settings. In-memory tokens
are cleared immediately when installation succeeds, fails, or is cancelled;
authentication is never retained between installations. The browser stays signed
in. Startup and local export need no sign-in.
See [authentication](docs/hugging-face-authentication.md).

## Model discovery

`PocketTtsModelDiscoveryService` discovers model candidates automatically from the
official `kyutai/pocket-tts` Hugging Face Hub JSON API without a hardcoded version
list. It requires internet access and preserves the exact repository commit,
candidate paths, and available size/Git/LFS metadata. Discovery and voice-cloning
compatibility are separate: all discovered candidates currently remain unverified.
This service is independent of WinUI and the runtime CLI. It performs no
authentication or model downloads and is not called during application startup.

See [model discovery](docs/model-discovery.md) for the observed 28 candidates,
revision details, limitations, and targeted verification command.

## Planned features

Planned features for the initial release:

- Broader management of supported Pocket TTS models.
- Broader management beyond the trusted installation catalog.
- Complete offline workflow validation with all required private model assets.

Local speech generation and preview playback are implemented. Voice library management
and importing existing voice profiles remain outside the current scope.

## How It Works

1. Launch Pocket TTS Studio lite and wait for local model verification.
2. Continue in Voice Cloning with the restored model (or the first usable model on first run), or explicitly choose an available model in the Model ComboBox. After removal, choose a replacement yourself. If none is available, install a compatible model in Settings.
3. Select local reference audio and an export folder in Export voice profile.
4. Use Export to create the automatically named native profile. Confirm Overwrite only when you intend to replace an existing file.

Missing assets block export until explicitly installed in Settings. Installation
does not activate a model automatically; return to Voice Cloning and select it. Startup uses the saved selection state, with automatic first-model activation only when no previous preference exists.
There are no automatic model downloads.
Actual offline inference still needs manual verification with a complete private installation.

## Technology

Pocket TTS Studio lite is built using:

- C# and .NET
- WinUI 3
- Official Kyutai Pocket TTS Rust runtime

The application uses official Pocket TTS model weights downloaded directly from Hugging Face.

Model weights are not included in the application distribution.

## Privacy

Pocket TTS Studio lite is designed for local voice processing.

- Reference audio is processed locally.
- Voice profiles are generated locally.
- Audio recordings are not uploaded to external services.
- Hugging Face credentials are not permanently stored.
- No account is required after the selected models have been downloaded.

## Responsible Use

Voice cloning must respect applicable laws, privacy rights, and the rights of individuals whose voices are used.

Only clone voices that you own or have explicit lawful permission to use.

Impersonation, fraud, deception, harassment, and other unauthorized or harmful uses are prohibited under the applicable Kyutai model conditions.

Users are responsible for ensuring that their use of voice cloning complies with applicable laws and model terms.

## Installation

Installation instructions and downloadable releases will be provided when the first public version becomes available.

## License

Pocket TTS Studio lite is released under the MIT License.

See [LICENSE](LICENSE) for details.

The application uses third-party components distributed under their respective licenses.

Kyutai Pocket TTS model weights are published under CC BY 4.0 and are subject to their applicable access and usage conditions.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for additional information.

## Acknowledgments

Pocket TTS is developed by Kyutai.

- [Pocket TTS on GitHub](https://github.com/kyutai-labs/pocket-tts)
- [Pocket TTS Models on Hugging Face](https://huggingface.co/kyutai/pocket-tts)

Pocket TTS Studio lite is an independent application and is not officially affiliated with or endorsed by Kyutai.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for release history.
