namespace PocketTTSStudioLite.Services;

public enum PocketTtsVoiceExportFailure
{
    None,
    Runtime,
    Connectivity,
    Audio,
    FileSystem,
    ModelAccessOrCache,
}

public sealed record PocketTtsVoiceExportProgress(string Message);

public sealed record PocketTtsVoiceExportResult(
    string DestinationPath, int ExitCode, PocketTtsVoiceExportFailure Failure, string Message)
{
    public bool Succeeded => ExitCode == 0 && Failure == PocketTtsVoiceExportFailure.None;
    public bool AuthenticationMayBeRequired => Failure == PocketTtsVoiceExportFailure.ModelAccessOrCache;
}

/// <summary>Classifies native output without retaining, returning, or displaying raw credential-bearing text.</summary>
internal sealed class PocketTtsExportOutput(IProgress<PocketTtsVoiceExportProgress>? progress)
{
    private readonly object _gate = new();
    private bool _reportedLoading;
    public PocketTtsVoiceExportFailure Failure { get; private set; } = PocketTtsVoiceExportFailure.Runtime;

    public void Observe(string text)
    {
        string lower = text.ToLowerInvariant();
        bool reportLoading = false;
        lock (_gate)
        {
            if (!_reportedLoading && (lower.Contains("loading model") || lower.Contains("loading ttsmodel weights")))
            { _reportedLoading = true; reportLoading = true; }
            PocketTtsVoiceExportFailure failure = lower switch
            {
                _ when lower.Contains("authentication required") || lower.Contains("unauthorized") ||
                    lower.Contains("forbidden") || lower.Contains("http error: 401") || lower.Contains("http error: 403") ||
                    lower.Contains("could not download the weights for the model with voice cloning") ||
                    lower.Contains("repository not found") => PocketTtsVoiceExportFailure.ModelAccessOrCache,
                _ when lower.Contains("permission denied") || lower.Contains("access is denied") ||
                    lower.Contains("no space left") || lower.Contains("not enough space") ||
                    lower.Contains("cannot write") || lower.Contains("cache lock timed out") => PocketTtsVoiceExportFailure.FileSystem,
                _ when lower.Contains("unsupported audio") || lower.Contains("unsupported codec") ||
                    lower.Contains("cannot decode") || lower.Contains("no audio track") ||
                    lower.Contains("cannot read audio") => PocketTtsVoiceExportFailure.Audio,
                _ when lower.Contains("connection") || lower.Contains("timed out") || lower.Contains("dns") ||
                    lower.Contains("network") || lower.Contains("error sending request") => PocketTtsVoiceExportFailure.Connectivity,
                _ => PocketTtsVoiceExportFailure.Runtime,
            };
            if ((int)failure > (int)Failure) { Failure = failure; }
        }
        if (reportLoading) { progress?.Report(new("The runtime is loading the verified private model assets…")); }
    }

    public string ErrorMessage(int exitCode) => Failure switch
    {
        PocketTtsVoiceExportFailure.ModelAccessOrCache =>
            "The verified private model assets could not be loaded. Return to Settings and check the installed model before retrying.",
        PocketTtsVoiceExportFailure.FileSystem =>
            "The runtime could not access or write a file. Check output/private storage permissions, free disk space, and other running Pocket TTS operations, then retry.",
        PocketTtsVoiceExportFailure.Audio =>
            "The runtime could not read this audio. Choose a valid supported audio recording (for example WAV, MP3, or FLAC) and retry.",
        PocketTtsVoiceExportFailure.Connectivity =>
            "Voice export requires only verified private files. Return to Settings and check the installed model and runtime before retrying.",
        _ => $"The runtime exited with code {exitCode}. Check the reference audio, installed private model, and output/private storage permissions, then retry.",
    };
}
