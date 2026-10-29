using PocketTTSStudioLite;
using PocketTTSStudioLite.Services;
using Windows.Media.Playback;

internal static class MemoryPlayerChecks
{
    internal static async Task RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string message)
        { if (!condition) { throw new InvalidOperationException(message); } passed++; }
        byte[] Wave(int frames, double frequency)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000);
            writer.Write(48000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 2);
            for (int i = 0; i < frames; i++) { writer.Write((short)(Math.Sin(i * 2 * Math.PI * frequency / 24000) * 8000)); }
            writer.Flush(); return stream.ToArray();
        }
        var first = await AudioPlaybackData.DecodeMemoryAsync(Wave(24000, 440), default);
        var second = await AudioPlaybackData.DecodeMemoryAsync(Wave(36000, 660), default);
        Check(!ReferenceEquals(first.Samples, second.Samples) && first.Duration != second.Duration, "Independent audio instances share samples or duration.");
        using var source1 = new ReferenceAudioPlaybackSource(first.Samples, first.SampleRate);
        using var source2 = new ReferenceAudioPlaybackSource(second.Samples, second.SampleRate);
        using var player1 = new MediaPlayer { AutoPlay = false, IsMuted = true };
        using var player2 = new MediaPlayer { AutoPlay = false, IsMuted = true };
        player1.CommandManager.IsEnabled = player2.CommandManager.IsEnabled = false;
        var open1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player1.MediaOpened += (_, _) => open1.TrySetResult();
        player2.MediaOpened += (_, _) => open2.TrySetResult();
        player1.MediaFailed += (_, failure) => open1.TrySetException(new Exception("First memory player failed: " + failure.ExtendedErrorCode));
        player2.MediaFailed += (_, failure) => open2.TrySetException(new Exception("Second memory player failed: " + failure.ExtendedErrorCode));
        player1.Source = source1.Source; player2.Source = source2.Source;
        await Task.WhenAll(open1.Task, open2.Task).WaitAsync(TimeSpan.FromSeconds(10));
        Check(Math.Abs(player1.PlaybackSession.NaturalDuration.TotalSeconds - first.Duration.TotalSeconds) < 0.005 &&
            Math.Abs(player2.PlaybackSession.NaturalDuration.TotalSeconds - second.Duration.TotalSeconds) < 0.005, "Memory-player duration differs from decoded sample counts.");
        player1.Play(); await Task.Delay(150); player1.Pause();
        Check(player2.PlaybackSession.Position == TimeSpan.Zero, "Playing one instance moved the other player.");
        var seek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player1.PlaybackSession.SeekCompleted += (_, _) => seek.TrySetResult();
        player1.PlaybackSession.Position = TimeSpan.FromSeconds(0.5);
        await seek.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Math.Abs(player1.PlaybackSession.Position.TotalSeconds - 0.5) < 0.05 && player2.PlaybackSession.Position == TimeSpan.Zero,
            "Seeking one memory player changed the other's independent state.");
        double paused = player1.PlaybackSession.Position.TotalSeconds;
        player2.Play(); await Task.Delay(150); player2.Pause();
        Check(Math.Abs(player1.PlaybackSession.Position.TotalSeconds - paused) < 0.01, "Playing the second instance moved the paused first player.");
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player1.MediaEnded += (_, _) => ended.TrySetResult();
        player1.PlaybackSession.Position = TimeSpan.Zero; player1.Play();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(player2.PlaybackSession.PlaybackState != MediaPlaybackState.Playing, "Ending one instance resumed the other player.");
        player1.Source = null; player2.Source = null;
        Console.WriteLine($"PASS: {passed} targeted in-memory Windows player checks. Two independent muted native players, duration, play/pause, seek, and completion; no GUI or audio files.");
    }
}
