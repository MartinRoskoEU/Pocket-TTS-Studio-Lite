using PocketTTSStudioLite;
using PocketTTSStudioLite.Services;

internal static class AudioWaveformDisplayChecks
{
    internal static void Run()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        static AudioEnvelope Bar(float amplitude) => new(-amplitude, amplitude);
        static double Variation(IReadOnlyList<AudioEnvelope> bars) => bars.Skip(1).Select((bar, index) => Math.Abs(bar.Maximum - bars[index].Maximum)).Sum();

        Check(AudioWaveformDisplay.Create([], 400).Length == 0, "Empty audio invented a waveform.");
        foreach (double width in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        { Check(AudioWaveformDisplay.Create([Bar(1)], width).Length == 0, "An invalid layout produced geometry data."); }
        Check(AudioWaveformDisplay.Create([Bar(0.5f)], 0.5).SequenceEqual(new[] { Bar(0.5f) }), "A tiny surface lost its only bar.");
        var alternating = Enumerable.Range(0, 200).Select(index => Bar(index % 2 == 0 ? 0.2f : 0.8f)).ToArray();
        var original = alternating.ToArray();
        var smoothed = AudioWaveformDisplay.Create(alternating, 400);
        Check(Variation(smoothed) < Variation(alternating) / 4, "Visual smoothing did not reduce jagged alternating peaks.");
        Check(alternating.SequenceEqual(original), "Display aggregation modified the source envelope.");
        Check(smoothed.All(bar => bar.Minimum >= -0.8f && bar.Maximum <= 0.8f && bar.Minimum <= bar.Maximum), "Smoothing invented amplification or inverted a bar.");
        Check(AudioWaveformDisplay.Create(alternating, 200).Length == 100 &&
            AudioWaveformDisplay.Create(alternating, 400).Length == 200 &&
            AudioWaveformDisplay.Create(alternating, 4000).Length == 200, "Display density does not follow width or exceeds the source resolution.");
        Check(AudioWaveformDisplay.Create(Enumerable.Repeat(Bar(0), 50).ToArray(), 100).All(bar => bar.Minimum == 0 && bar.Maximum == 0),
            "Silent audio acquired a fake waveform.");
        var gap = AudioWaveformDisplay.Create([Bar(0.8f), Bar(0), Bar(0.8f)], 6);
        Check(gap[1] == Bar(0) && gap[0].Maximum > 0 && gap[2].Maximum > 0, "Smoothing filled a silence gap or erased neighboring speech.");
        var quietThenSpeech = Enumerable.Repeat(Bar(0.01f), 20).Concat(Enumerable.Repeat(Bar(0.7f), 20)).ToArray();
        var levels = AudioWaveformDisplay.Create(quietThenSpeech, 80);
        Check(levels[10].Maximum < 0.02f && levels[30].Maximum > 0.6f, "Quiet and speech regions lost their amplitude distinction.");
        var transient = AudioWaveformDisplay.Create([Bar(0), Bar(0), Bar(0), Bar(1)], 4);
        Check(transient[0] == Bar(0) && transient[1].Maximum > 0, "Peak aggregation dropped a final-bin transient or misplaced it in time.");
        var asymmetric = AudioWaveformDisplay.Create([new AudioEnvelope(-0.9f, 0.2f), new AudioEnvelope(-0.5f, 0.1f)], 4);
        Check(asymmetric.All(bar => Math.Abs(bar.Minimum) > bar.Maximum), "Smoothing replaced the actual bipolar envelope with a symmetric invented signal.");
        var decoded = new MediaFoundationAudioDecoder.Decoded(Enumerable.Range(0, 4800).Select(index => (float)Math.Sin(index / 20.0)).ToArray(), 24000);
        var playback = AudioPlaybackData.Create(decoded, default);
        var samples = playback.Samples.ToArray();
        var envelope = playback.Waveform.ToArray();
        _ = AudioWaveformDisplay.Create(playback.Waveform, 360);
        Check(playback.Samples.SequenceEqual(samples) && playback.Waveform.SequenceEqual(envelope) && playback.Duration.TotalSeconds == 0.2,
            "Visualization changed decoded PCM, the precise envelope, or playback duration.");
        Console.WriteLine($"PASS: {passed} targeted shared-waveform display checks. Density, smoothing, silence/transient preservation, and unchanged PCM/duration; no GUI or files.");
    }
}
