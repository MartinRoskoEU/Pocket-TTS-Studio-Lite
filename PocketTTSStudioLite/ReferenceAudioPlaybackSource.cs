using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Security.Cryptography;

namespace PocketTTSStudioLite;

/// <summary>Seekable in-memory PCM for Windows playback; never writes a preview conversion to disk.</summary>
internal sealed class ReferenceAudioPlaybackSource : IDisposable
{
    private readonly MediaStreamSource _stream;
    private readonly float[] _samples;
    private readonly int _sampleRate;
    private readonly object _gate = new();
    private int _position;
    private bool _disposed, _discontinuous = true;
    internal MediaSource Source { get; }

    internal ReferenceAudioPlaybackSource(float[] samples, int sampleRate)
    {
        _samples = samples; _sampleRate = sampleRate;
        var encoding = AudioEncodingProperties.CreatePcm((uint)sampleRate, 1, 32);
        encoding.Subtype = MediaEncodingSubtypes.Float;
        _stream = new MediaStreamSource(new AudioStreamDescriptor(encoding))
        { CanSeek = true, Duration = TimeSpan.FromSeconds(samples.Length / (double)sampleRate), BufferTime = TimeSpan.Zero };
        _stream.Starting += Starting;
        _stream.SampleRequested += SampleRequested;
        Source = MediaSource.CreateFromMediaStreamSource(_stream);
    }
    private void Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            if (args.Request.StartPosition is { } position)
            { _position = (int)Math.Clamp(Math.Round(position.TotalSeconds * _sampleRate), 0, _samples.Length); }
            _discontinuous = true;
            args.Request.SetActualStartPosition(TimeSpan.FromSeconds(_position / (double)_sampleRate));
        }
    }
    private void SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || _position >= _samples.Length) { args.Request.Sample = null; return; }
            int count = Math.Min(2048, _samples.Length - _position);
            byte[] bytes = new byte[count * 4];
            Buffer.BlockCopy(_samples, _position * 4, bytes, 0, bytes.Length);
            var sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes), TimeSpan.FromSeconds(_position / (double)_sampleRate));
            sample.Duration = TimeSpan.FromSeconds(count / (double)_sampleRate);
            sample.Discontinuous = _discontinuous;
            args.Request.Sample = sample;
            _position += count; _discontinuous = false;
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) { return; } _disposed = true; }
        _stream.Starting -= Starting;
        _stream.SampleRequested -= SampleRequested;
        Source.Dispose();
    }
}
