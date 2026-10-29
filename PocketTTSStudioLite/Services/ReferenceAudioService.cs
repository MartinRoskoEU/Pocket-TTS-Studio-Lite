namespace PocketTTSStudioLite.Services;

internal sealed record AudioEnvelope(float Minimum, float Maximum);
internal record AudioPlaybackData(int SampleRate, float[] Samples, IReadOnlyList<AudioEnvelope> Waveform, float Peak)
{
    internal TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
    internal static AudioPlaybackData Create(MediaFoundationAudioDecoder.Decoded decoded, CancellationToken cancellationToken)
    {
        int buckets = Math.Min(1024, decoded.Samples.Length);
        var envelope = new AudioEnvelope[buckets];
        float peak = 0;
        for (int bucket = 0; bucket < buckets; bucket++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = (int)((long)bucket * decoded.Samples.Length / buckets), end = (int)((long)(bucket + 1) * decoded.Samples.Length / buckets);
            float minimum = float.MaxValue, maximum = float.MinValue;
            for (int index = start; index < end; index++) { minimum = Math.Min(minimum, decoded.Samples[index]); maximum = Math.Max(maximum, decoded.Samples[index]); }
            envelope[bucket] = new(minimum, maximum);
            peak = Math.Max(peak, Math.Max(Math.Abs(minimum), Math.Abs(maximum)));
        }
        return new(decoded.SampleRate, decoded.Samples, envelope, peak);
    }
    internal static Task<AudioPlaybackData> DecodeMemoryAsync(byte[] wav, CancellationToken cancellationToken) =>
        Task.Run(() => Create(MediaFoundationAudioDecoder.DecodeMemory(wav, cancellationToken), cancellationToken), cancellationToken);
}

internal sealed record PreparedReferenceAudio(string SourcePath, FileStream SourceLease, int SampleRate,
    float[] Samples, IReadOnlyList<AudioEnvelope> Waveform, float Peak) : AudioPlaybackData(SampleRate, Samples, Waveform, Peak), IDisposable
{
    private readonly object _gate = new();
    private ReferenceAudioStore? _storage;
    private bool _disposed, _preserve;
    internal bool RuntimeNeedsFallback { get; set; }
    internal string? RuntimeIdentity { get; set; }
    internal bool UsesOriginal => !RuntimeNeedsFallback;
    internal string InputPath => UsesOriginal ? SourcePath : _storage?.FallbackPath ?? throw new InvalidOperationException("Audio fallback has not been prepared.");
    internal bool CleanupIncomplete => _storage?.CleanupIncomplete == true;
    internal bool PreserveTemporaryFiles
    {
        set { lock (_gate) { _preserve = value; if (_storage is not null) { _storage.PreserveFiles = value; } } }
    }
    internal string EnsureFallbackWav(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_storage is not null) { return _storage.FallbackPath; }
            var storage = new ReferenceAudioStore { PreserveFiles = _preserve };
            try { storage.WriteWav(storage.FallbackPath, Samples, SampleRate, cancellationToken); _storage = storage; return storage.FallbackPath; }
            catch { storage.Dispose(); throw; }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _storage?.Dispose(); SourceLease.Dispose();
        }
    }
}

internal sealed class ReferenceAudioService(ReferenceAudioFormats formats)
{
    internal Task<PreparedReferenceAudio> PrepareAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Prepare(path, cancellationToken), cancellationToken);
    private PreparedReferenceAudio Prepare(string path, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc))
        { throw new ArgumentException("Choose one local audio file, not a URL or network share."); }
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) { throw new ArgumentException("Choose a local audio file on this computer."); }
        var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        PreparedReferenceAudio? prepared = null;
        try
        {
            var decoded = MediaFoundationAudioDecoder.Decode(full, formats, cancellationToken);
            var playback = AudioPlaybackData.Create(decoded, cancellationToken);
            prepared = new(full, source, playback.SampleRate, playback.Samples, playback.Waveform, playback.Peak);
            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }
        catch { prepared?.Dispose(); source.Dispose(); throw; }
    }
}
