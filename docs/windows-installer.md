# Windows installer and release version

The Inno Setup installer is an unpackaged, per-user Windows x64 distribution.
It uses the existing application and runtime services unchanged. No automatic
application updater, model payload, model download, or audio file is included.

## Version and build

`Directory.Build.props` is the **one release-version source**. Change its `Version`
property for a future release. Assembly/file versions and package metadata derive
their fourth component from it; informational/product version and HTTP User-Agent
use the three-part release version. MSBuild stamps intermediate native and package
manifests; their source `0.0.0.0` values are template placeholders. The custom title
bar and window title display the built release version.

`installer/build.ps1` evaluates the application project's MSBuild `Version`, checks
the published EXE metadata, and passes `--define=AppVersion=...` to Inno Setup.
The `.iss` deliberately fails without this parameter. Installer file/product
metadata, wizard display, Windows uninstall DisplayVersion, and the setup EXE
filename therefore use the same value.

Build on Windows x64 with the .NET 10 SDK, Windows SDK/project build prerequisites,
Visual Studio C++ x64 build tools, and the official Inno Setup **7 or later** compiler:

```powershell
.\installer\build.ps1 -IsccPath 'C:\Program Files (x86)\Inno Setup 7\ISCC.exe'
```

An official portable compiler directory is also supported. The script does not
download/install build tools or run the resulting installer. Runtime input defaults
to the project's existing Git-ignored development payload. To supply a prepared
official archive from another directory:

```powershell
.\installer\build.ps1 -IsccPath 'C:\tools\inno\ISCC.exe' `
    -PocketTtsRuntimeSourceDir 'C:\prepared\pocket-tts-windows-x64'
```

The pinned official v4.0.1 executable and its adjacent `LICENSE` are **mandatory**.
Missing files or an executable hash different from production's validated runtime
identity fail packaging. Its internal `--version` is 3.3.0; that is not the release
identity. No native runtime or model is downloaded by the packaging script.

Each run uses fresh staging under `artifacts/installer/staging/<guid>/`, publishes
with `Properties/PublishProfiles/installer-x64.pubxml`, builds the installer-only CPU
check, audits native direct/delayed imports, collects licenses, and runs ISCC.
Final output: `release/PocketTTSStudioLite-<Version>-win-x64-Setup.exe` at the
repository root. The build passes this directory to Inno Setup's native `OutputDir`
configuration; the compiler writes the EXE there directly. Staging, tools, existing
outputs and diagnostic files under `artifacts/` are preserved, including
`native-imports.csv`. Both `release/` and `artifacts/` remain ignored by Git; only
installer sources and required notices belong in source control.

## Installation and prerequisites

- Traditional native Windows wizard, with the application's icon and MIT license.
- Default `%LOCALAPPDATA%\Programs\Pocket TTS Studio lite`; no administrator
  privileges requested for normal installation. The native Select Destination
  Location page is already enabled for fresh installs (`DisableDirPage=auto`),
  allowing a different folder. Existing upgrade behavior is preserved: the previous
  directory is reused and that page is skipped when a prior installation is found.
- The native Select Additional Tasks page offers independent Start Menu (checked
  initially) and Desktop (unchecked initially) shortcut choices. Start Menu selects
  both the application and existing uninstall shortcuts. All shortcuts belong to
  the current user. Native `UsePreviousTasks=yes` restores choices on upgrades;
  unchecked choices also remove the corresponding application-owned shortcuts
  left by earlier installations. The standard uninstaller removes installed
  shortcuts. One per-user Windows uninstall entry is created regardless of shortcut
  selection.
- Native x64 Windows 10 build 17763 or later, with AVX2 **and FMA** and OS-enabled
  AVX state. The tiny baseline-x64 installer helper checks CPUID and XGETBV before
  installation. ARM64/x86 operating systems and incompatible CPUs are rejected.
  The app's existing processor checks remain in place.
- .NET is published self-contained (`SelfContained=true`); Windows App SDK is also
  app-local (`WindowsAppSDKSelfContained=true`, `WindowsPackageType=None`). Shared
  .NET/Windows App SDK runtime installers, MSIX registration, and administrator
  elevation are unnecessary. These bundled components are updated by rebuilding
  and distributing a newer application installer.
- Native import inspection found no shared VC++ 14 runtime dependency in this
  payload, including Pocket TTS and the helper. Windows supplies the inbox UCRT.
  A future VC++ dependency causes packaging to fail for explicit provisioning;
  this installer does not download an unnecessary VC++ redistributable.
- Windows Media Foundation is checked using DLLs loaded from System32. Existing
  media components are left alone. If missing on Windows N, setup invokes the
  official Windows `DISM /Online /Add-Capability` Media Feature Pack mechanism.
  Only that system prerequisite requests UAC. Windows Update/enterprise servicing
  supplies and verifies Microsoft's OS-matched files; no third-party download or
  custom prerequisite installer is used. Failure/cancelled approval stops before
  application file installation with an actionable error. Successful servicing
  requires a restart, which setup requests; setup does not launch the application.
  Offline/blocked Windows Update, older N editions without this capability, or
  damaged non-N media components require administrator/OS servicing remediation.
  Additional optional codecs still depend on Windows, as before.

## Upgrades and private data

The stable Inno `AppId` is `{336B2514-DBF3-4566-9302-95EE5D2D7DD3}`. **Never change
it between versions.** The next installer reuses the previous install directory
and per-user uninstall entry. Native Inno/Windows Restart Manager handles files in
use; the installer does not introduce custom process termination. Do not run the
old uninstaller as an upgrade step.

Models, settings, caches, installed runtime updates, and other private data live in
`%LOCALAPPDATA%\PocketTTSStudioLite`, outside the installed program directory.
They are neither replaced nor deleted on upgrade. No shared Hugging Face cache or
user-selected export destination is inspected or modified by setup.

## Uninstallation

Windows Settings / Installed apps and the Start Menu uninstall shortcut invoke the
standard Inno uninstaller. It removes installed files, its shortcuts, and its
Windows uninstall entry. No shared Microsoft component is uninstalled.

Interactive uninstall separately asks whether to permanently remove the **complete**
private data directory, showing its full path and warning about downloaded models,
runtimes, settings and caches. **No** is the default. Silent uninstall always keeps
private data. Cancellation before uninstall does not remove private data.

Only the fixed application-owned child of native `{localappdata}` may be cleaned;
no settings/inventory/export path is accepted as a deletion target. The uninstaller
pins the entire ancestor chain and root using Windows directory handles that deny
writes/rename/deletion, and rejects reparse/redirected roots. Native Inno `DelTree` removes
descendant reparse entries without traversing their targets. Handles are released
before the final nonrecursive root removal. Busy files or failed safety checks
produce a cleanup error instead of broadening the deletion target. Exported voice
profiles in external folders and shared caches/runtimes are preserved.

## Redistribution and validation limits

Application MIT, original Microsoft/NuGet/.NET notices, upstream runtime MIT and
third-party Rust notices, and the Inno license are installed with the application.
The original Pocket TTS archive only provides its top-level MIT license. The
additional reviewed notice collection covers all 460 upstream Cargo.lock packages
conservatively, including dependencies not used on Windows. Crate archives were
checked against locked SHA256 hashes. Missing archive notices are supplemented
from recorded upstream commits; `realfft`'s license comes from a newer immutable
repository commit because its locked source omits the license file. The 18 MPL-2.0
packages also include complete, original source archives and a source-availability
notice. Permissive alternatives are used for dual-licensed packages; the app's MIT
license is unchanged. See [third-party notices](../THIRD_PARTY_NOTICES.md).

The first setup EXE is **unsigned**. Configure publisher code signing and timestamping
for setup and its uninstaller before public distribution; unsigned downloads can
trigger Windows SmartScreen. No certificate, signing key, commit, tag, or release
is created by this build. Windows 10 support/security updates are Microsoft's
responsibility; the minimum API version is not a promise of support for every
out-of-support Windows edition.

Release app/publish and Inno compilation, metadata/payload/license gates, native
import scanning, and targeted checks are build-time evidence. Installation,
upgrade, uninstall/data deletion, Windows N servicing/UAC/reboot, and launch on a
clean machine must still be verified in disposable Windows VMs. None is exercised
on the development PC. No application GUI is launched by packaging.

Build verification on 2026-10-29: application Release build (zero warnings/errors),
self-contained Release publish, and Inno compilation succeeded. Eight existing
mocked model-discovery check groups passed, including the version-derived
User-Agent assertion; ModelRemovalChecks built without warnings/errors after its
linked-source update. Read-only native CPU/media checks passed on this machine,
all 18 MPL source archive hashes matched, and missing/tampered runtime packaging
inputs were rejected before publish. The native import audit covered 183 files
with imports and found no VC++ 14 runtime imports. The built EXE is unsigned.

Official references: [Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps),
[Visual C++ redistribution](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files),
[Media Feature Pack](https://support.microsoft.com/windows/experience/platform-variants/media-feature-pack-for-windows-n),
[DISM capabilities](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-capabilities-package-servicing-command-line-options),
[Inno reparse-safe DelTree](https://jrsoftware.org/ishelp/topic_isxfunc_deltree.htm).
