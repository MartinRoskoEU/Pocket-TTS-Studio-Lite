# Third-party notices

Pocket TTS Studio lite source code is licensed under MIT; see [LICENSE](LICENSE).
Third-party components retain their own licenses. The application's MIT license
text in `LICENSE` is unchanged.

## Windows installer redistribution

The Inno Setup distribution includes the application MIT license, the original
resolved NuGet/.NET license and notice files under `licenses/`, and the runtime's
original MIT `LICENSE` beside `runtime/pocket-tts/pocket-tts.exe`. The wizard's
license page shows only the application license; third-party notices do not add
extra acceptance pages. See [installer documentation](docs/windows-installer.md).

.NET 10 and Windows App SDK are published self-contained. Their unchanged binary
package terms/notices govern the bundled framework components. Native direct and
delay-import scanning found no separate VC++ redistributable requirement. Windows
UCRT and Media Foundation remain OS components; missing Media Feature Pack on
Windows N is provisioned through Microsoft's Windows servicing mechanism, not a
redistributed third-party codec. No model weights are bundled or fetched by setup.

Inno Setup 7.1.0 is the build tool used for the initial installer. Its original
copyright/website notices remain in the unmodified setup engine and its license is
included at `licenses/Inno-Setup-license.txt`. Its [license](https://github.com/jrsoftware/issrc/blob/is-7_1_0/license.txt)
permits redistribution under the stated notice/origin conditions.

The official Pocket TTS Rust executable statically incorporates third-party Rust
components; its top-level MIT license is not their only license. The reviewed
collection in `installer/licenses/pocket-tts/` retains original license texts,
attribution manifests, exact lockfile versions/checksums, and supplemental upstream
notice provenance. It conservatively includes all 460 lockfile packages, including
build-only/non-Windows dependencies, and is copied into the installed `licenses/`.
Some published crates omit license files; their original repository notices were
retrieved at recorded commits. `realfft` requires a newer immutable repository
license snapshot because its original source commit omitted that file.

The 18 MPL-2.0 packages (Symphonia and its decoder/container crates, `colored`, and
`option-ext`) include unchanged complete published source archives, verified against
Cargo.lock, plus `SOURCE-AVAILABILITY.txt`. The original sources and their notices
remain available to recipients beside the binary. MPL does not change this
application's MIT source license. Other dependencies include permissive MIT,
Apache-2.0, BSD, ISC, Unicode, Zlib, BSL and CDLA terms retained in the collection;
permissive alternatives are selected for multi-licensed packages, not LGPL options.

## Direct dependencies and development tooling

| Component | Version | Applicable license |
| --- | --- | --- |
| .NET | 10 | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and upstream third-party notices |
| Microsoft.WindowsAppSDK | 2.5.1 | Microsoft Windows App SDK software license terms, supplied as `license.txt` in the NuGet package; additional notices in `NOTICE.txt` |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | [Microsoft Windows SDK license terms](https://aka.ms/WinSDKLicenseURL) |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.7.1 | [MIT](https://github.com/microsoft/WinAppCli/blob/e3cd0b94f3e7b776ac51ba0564f4908094e979b5/LICENSE) |
| Microsoft.WindowsAppSDK.WinUI.CSharp.Templates | 0.0.7-alpha | Official Microsoft project-generation tooling; [Windows App SDK source license](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE) |

The template pack is installed as development tooling and is not an application
package reference. The project uses its own application artwork and the WinUI
template's packaging structure. The Windows App SDK source repository's MIT license does not replace the
license terms included with Microsoft's binary packages.

## Transitive NuGet dependencies

The Windows App SDK metapackage resolves the following components. Their inclusion
does not mean the application implements AI, ML, widgets, search, or web content.

| Component | Version | Applicable package license |
| --- | --- | --- |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.AI | 2.5.5 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.ML | 2.1.94 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.Search | 2.5.5 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.WindowsAppSDK.Runtime | 2.5.1 | Microsoft Windows App SDK terms (`license.txt`) |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | Microsoft Windows ML Runtime terms (`license.txt`) |
| Microsoft.Web.WebView2 | 1.0.3719.77 | Package-provided redistribution license (`LICENSE.txt`) |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | Microsoft Windows SDK terms (`sdk_license.txt`) |
| System.Numerics.Tensors | 9.0.0 | MIT |

NuGet restores the license and notice files into its package cache. Consult each
resolved package's license and notices when distributing its binaries. The .NET
SDK also supplies Windows SDK reference assemblies and build-time dependencies
under their respective included license terms.

## Kyutai Pocket TTS Rust runtime

The official Windows x64 executable from [Kyutai release v4.0.1](https://github.com/kyutai-labs/pocket-tts/releases/tag/v4.0.1)
is prepared in the Git-ignored `development/runtime/` directory and staged beside
the application as the unchanged trusted fallback. It reports `pocket-tts 3.3.0`.
Startup verifies local runtime identity and runs model-free CLI diagnostics.
User-confirmed runtime updates are stored separately in local application data;
each updated executable retains the `LICENSE` extracted from its verified official
release archive. Consult that file for the installed release's applicable terms.

The release archive includes an MIT `LICENSE`, retained beside `pocket-tts.exe`.
See the [release-pinned upstream license](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/LICENSE)
and [Rust manifest](https://github.com/kyutai-labs/pocket-tts/blob/a922b912dfa366975294afb568d815b305acde5d/rust/Cargo.toml).
Keep upstream license notices with any future runtime redistribution; the
application's MIT license is unchanged.

The runtime builds on xn-ptts and its Rust dependencies. No new NuGet dependency
was added. Hugging Face Hub metadata discovery uses the public HTTPS JSON API
through .NET's existing HTTP/JSON libraries; no Hub SDK or Python dependency is
included. No model weights are included. See
[runtime validation](docs/pocket-tts-runtime.md) for artifact details and checks.

User-requested model installation downloads only pinned required files from the
official `kyutai/pocket-tts` repository, using existing .NET HTTP/cryptography APIs
and the existing OAuth service. Models remain separate from application files
in private `%LOCALAPPDATA%\PocketTTSStudioLite\models` storage and retain their upstream terms; signing in does
not accept model conditions. No weights are redistributed with this feature and
no additional dependency or license change is introduced. See
[model installation](docs/model-installation.md) for metadata provenance and storage.

Shared Hugging Face caches are not searched, imported, or written by the
application. Missing models download directly into private staging; only
verified private blobs are reused. The private content store uses only
existing .NET file/cryptography APIs and supported Windows file-handle APIs to
reject links/external paths. No additional package, SDK, bundled weights, or
license change is introduced. See [private model storage](docs/private-model-storage.md).

Private model removal and native process coordination use existing .NET file/JSON
APIs and supported Windows file-handle/process/job APIs. No additional package,
SDK, model redistribution, or license change is introduced. See
[model removal](docs/model-removal.md).

Automatic and manual anonymous model update checking uses the same .NET HTTP/JSON APIs against
official Hugging Face model-info/tree metadata, without an SDK, credentials, new
dependencies, model downloads, or license changes. See
[model update checks](docs/model-update-checks.md).
Successful check timestamps and identity-bound results use the existing .NET
JSON/cryptography/file APIs in local application data; no new dependency is added.

## Hugging Face OAuth

Authentication uses Hugging Face's official public OAuth HTTPS endpoints with
the existing .NET HTTP/JSON/time APIs and Windows App SDK browser-launch APIs.
No OAuth SDK, Python dependency, or additional NuGet package was added. The public
client registration is application configuration, not a secret. See
[authentication documentation](docs/hugging-face-authentication.md). Existing
third-party licenses and the application's MIT license remain unchanged.

Authentication is presented only in a native installation dialog when a requested
model download requires it. Its in-memory session is cleared after every
installation outcome; clearing it does not sign out the user's browser.

## Native voice export

The workflow reuses the already verified Kyutai executable and Windows App SDK
file pickers. No downloader, audio/voice conversion library, or new NuGet package
was added. Its existing native Hub dependency is `hf-hub` 1.0.0
([Apache-2.0 package](https://docs.rs/crate/hf-hub/1.0.0)); it is not a C# dependency.
See [voice export](docs/voice-export.md) for source-pinned runtime behavior and
manual validation limits. Model weights are still not bundled with the app.

## Runtime-pinned setup metadata

The application embeds official Pocket TTS YAML configs from source commit
`a922b912dfa366975294afb568d815b305acde5d` under the upstream MIT license,
retained at `PocketTTSStudioLite/RuntimeMetadata/v4.0.1/LICENSE`. Raw official
Hugging Face Hub metadata snapshots supply exact revisions and available hashes
for offline validation. No weights are bundled and no new dependency was added.
The existing validated runtime and its MIT notice are copied app-relative by both
Debug and Release builds. See [model setup](docs/model-setup.md).

## Reference audio

Reference decoding uses Windows Media Foundation source-reader/decoder APIs and
installed Windows media components. Playback uses the Windows `MediaPlayer` and
`MediaStreamSource` APIs. Windows Registry, COM, file-handle operations, and WAV
writing use existing Windows/.NET APIs. No decoder library, codec, new NuGet
package, or Pocket TTS preprocessing implementation is bundled with this feature.
Optional system codecs remain governed by their own distributors' terms.
Targeted fixture checks use an existing external FFmpeg executable solely to
create synthetic local recordings; FFmpeg is not shipped or required by the app.
See [reference audio](docs/reference-audio.md).

Reference audio and generated Voice preview share a WinUI UserControl and the same
Windows Media Foundation decoder/playback implementation. Generated WAV bytes are
decoded using the Windows `SHCreateMemStream` COM memory stream API, without audio
files. Local generation and prepared-voice reuse use the existing MIT-licensed
Pocket TTS Rust executable's official loopback `serve`/`/tts` interface and the .NET
HTTP client. No new dependency, inference library, codec, model weight, or custom
voice-state format is bundled. See [generation and preview](docs/voice-generation.md).
