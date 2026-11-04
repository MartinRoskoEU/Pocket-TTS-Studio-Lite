# Pocket TTS Studio lite

A Windows desktop application for cloning voices with Kyutai Pocket TTS.
Import a reference recording, generate a local speech preview, and export a native
`.safetensors` voice profile.

**Version 0.2.1** · [Download](https://github.com/MartinRoskoEU/Pocket-TTS-Studio-Lite/releases) · [Changelog](CHANGELOG.md) · [Documentation](docs/README.md)

## Features

- Import reference audio with waveform, playback and seeking.
- Generate speech previews locally from your own text.
- Export native voice profiles using the official Pocket TTS Rust runtime.
- Install supported voice-cloning models and select a verified installed model.
- Check for model and runtime updates from one Settings action; confirm each update
  before downloading it.
- Use offline voice cloning after installing the required model and runtime.
- Install for the current Windows user, with a choice to retain or remove verified
  private data during uninstall.

Version 0.2.1 fixes window minimization crashes and improves dialog cancellation
and preview-generation error handling. See the [release notes](docs/releases/0.2.1-notes.md).

## Requirements

- Windows 10 version 1809 or later, or Windows 11, on x64.
- An x64 processor with AVX2 and FMA support.
- Windows Media Foundation; Windows N editions require the Media Feature Pack.
- Internet access for initial model downloads and update checks/downloads.
- A Hugging Face account with the required model access when a gated download
  requires authorization.

The installer includes .NET, Windows App SDK and the official Pocket TTS runtime.
Model weights are downloaded separately. It installs per user; enabling the Media
Feature Pack on Windows N may require administrator approval.

## Installation and use

1. Download `PocketTTSStudioLite-0.2.1-win-x64-Setup.exe` from
   [GitHub Releases](https://github.com/MartinRoskoEU/Pocket-TTS-Studio-Lite/releases)
   and run it. The current installer is unsigned.
2. In **Settings**, install a supported model. Complete Hugging Face authorization
   and review the upstream model conditions when required.
3. Open **Voice Cloning**, select an installed model and import reference audio.
4. Enter preview text and choose **Generate** to listen to a local preview.
5. Choose an export folder and select **Export** to save the voice profile.
   Generating a preview is optional for export.

Updates retain the selected model and retire verified superseded files after a
successful update. The bundled runtime remains available as a fallback. There is
no automatic application updater. Reinstalling preserves private data; uninstall
keeps it by default and offers removal of verified application-managed data.
External voice profiles, shared caches and unknown files are preserved.

## Privacy and responsible use

Audio processing and voice profile creation run locally. Reference recordings are
not uploaded, and Hugging Face authentication tokens are not permanently stored.

Only clone voices you own or have permission to use, and follow the applicable
model conditions and the rights of the people whose voices you use.

## Documentation and development

Start with the [documentation index](docs/README.md) for building, authentication,
model/runtime management, voice cloning and release validation. The project uses
C#, .NET 10 and WinUI 3. Build and packaging instructions are in
[Windows installer and build](docs/windows-installer.md).

## License and acknowledgments

Application source: [MIT](LICENSE). Bundled components and separately downloaded
models retain their own terms; see [third-party notices](THIRD_PARTY_NOTICES.md).

[Pocket TTS](https://github.com/kyutai-labs/pocket-tts) is developed by Kyutai.
This is an independent application and is not affiliated with or endorsed by Kyutai.
