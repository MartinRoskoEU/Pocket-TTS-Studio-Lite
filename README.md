# Pocket TTS Studio lite

Pocket TTS Studio lite is a free, open-source desktop application for creating custom voice profiles using Kyutai Pocket TTS.

The application provides a simple graphical interface for voice cloning without requiring users to work with command-line tools.

Voice profiles are exported in the native `.safetensors` format.

## Project Status

**In development — Version 0.1.0**

The initial release will focus on voice cloning and model management.

## Features

Planned features for the initial release:

- Modern Windows desktop interface.
- Hugging Face authentication for gated model access.
- Download and management of supported Pocket TTS models.
- Selection between installed models.
- Import of reference audio recordings.
- Voice cloning using official Kyutai models.
- Export of voice profiles in `.safetensors` format.
- Offline operation after model installation.

The initial release will not include text-to-speech generation, voice playback, or voice library management.

## How It Works

1. Launch Pocket TTS Studio lite.
2. Connect your Hugging Face account.
3. Accept the required model access conditions.
4. Select and download a supported Pocket TTS model.
5. Import a reference audio recording.
6. Create and export your voice profile.

Hugging Face authentication is only required when downloading models.

Once a model is installed, voice cloning works locally without an internet connection.

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