# Pocket TTS Studio lite — Development Instructions

## Project Overview

Pocket TTS Studio lite is a free, open-source Windows desktop application for creating voice profiles using Kyutai Pocket TTS.

The application focuses on simplicity, reliability, and offline operation after the required models have been downloaded.

The application source code is licensed under MIT.

## Technology Stack

- Language: C#
- Framework: .NET
- User interface: WinUI 3
- TTS runtime: Official Kyutai Pocket TTS Rust executable
- Model distribution: Hugging Face
- Voice profile format: Safetensors

Use official, actively maintained APIs and components.

## Application Scope

The initial release focuses exclusively on voice cloning.

Core functionality:

- Hugging Face authentication.
- Downloading and managing supported Pocket TTS models.
- Selecting an installed model.
- Importing reference audio.
- Creating voice profiles.
- Exporting native `.safetensors` files.

Do not introduce text-to-speech playback, voice libraries, or importing existing voice profiles without explicit approval.

## Model Management

- Use official Kyutai model weights.
- Allow users to select supported model versions.
- Download gated models using the user's authorized Hugging Face account.
- Keep installed models separate from application files.
- Preserve exact model revisions for reproducibility.
- Do not bundle gated model weights with the application.
- Do not implement automatic application updates.

Model downloads require internet access. Voice cloning must work offline once the selected model is installed.

## Authentication and Security

- Use official Hugging Face OAuth mechanisms.
- Never request or handle user passwords.
- Do not permanently store authentication tokens.
- Do not expose credentials in logs.
- Keep reference audio and generated voice profiles local.
- Do not upload user audio to external services.

## Voice Profile Export

Use the official Pocket TTS runtime to create voice profiles.

Preserve the native `.safetensors` format without proprietary modifications.

Do not introduce custom voice containers or unnecessary conversions.

## Development Principles

- Prefer correctness, stability, and maintainability.
- Follow established project architecture and naming conventions.
- Keep UI, model management, authentication, and voice cloning logic separate.
- Handle failures explicitly and provide clear user-facing errors.
- Keep long-running operations asynchronous to avoid blocking the UI.
- Avoid unnecessary dependencies and abstractions.
- Do not introduce unrelated features or refactoring.
- Use production-ready implementations only.

## Testing

Tests must focus on actual application requirements.

Verify critical functionality, error handling, model downloads, and voice profile exports.

Avoid unnecessary test infrastructure.

## Documentation

Keep project documentation synchronized with implementation changes.

Maintain:

- README.md
- CHANGELOG.md
- LICENSE
- THIRD_PARTY_NOTICES.md

Document third-party dependencies and their applicable licenses.

Do not modify the MIT license without explicit approval.