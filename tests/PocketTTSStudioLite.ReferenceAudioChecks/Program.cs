using PocketTTSStudioLite;
using PocketTTSStudioLite.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

if (args.Contains("--memory-player-only", StringComparer.Ordinal))
{
    await MemoryPlayerChecks.RunAsync();
    return;
}

if (args.Contains("--filtering-only", StringComparer.Ordinal))
{
    await ReferenceAudioFilteringChecks.RunAsync();
    return;
}

if (args.Length != 1 || !File.Exists(args[0])) { throw new ArgumentException("Pass an existing FFmpeg executable for fixture generation only. It is not an application dependency."); }
string fixtures = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-ReferenceChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtures);
PocketTtsRuntimeStore.LocalDataPath = Path.Combine(fixtures, "app-data");
int passed = 0;
void Check(bool valid, string message) { if (!valid) { throw new Exception(message); } }
async Task<string> Generate(string name, string codec, int rate = 44100, int channels = 2, double seconds = 0.4, string? signal = null, params string[] extra)
{
    string path = Path.Combine(fixtures, name);
    var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", signal ?? $"sine=frequency=440:sample_rate={rate}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-ar", rate.ToString(), "-ac", channels.ToString(), "-c:a", codec }.Concat(extra).Append(path))
    { start.ArgumentList.Add(argument); }
    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync(); await output;
    Check(process.ExitCode == 0, "Fixture generation failed: " + await error);
    return path;
}
float[] ReadPrepared(string path, int rate)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    Check(new string(reader.ReadChars(4)) == "RIFF", "Prepared WAV missing RIFF."); reader.ReadInt32();
    Check(new string(reader.ReadChars(8)) == "WAVEfmt " && reader.ReadInt32() == 16, "Unexpected prepared WAV format.");
    Check(reader.ReadInt16() == 3 && reader.ReadInt16() == 1 && reader.ReadInt32() == rate, "Fallback WAV rate or format differs from decoded samples.");
    Check(reader.ReadInt32() == rate * 4 && reader.ReadInt16() == 4 && reader.ReadInt16() == 32, "Prepared PCM stride is incorrect.");
    Check(new string(reader.ReadChars(4)) == "fact" && reader.ReadInt32() == 4, "Prepared float sample count missing.");
    int count = reader.ReadInt32();
    Check(new string(reader.ReadChars(4)) == "data" && reader.ReadInt32() == count * 4, "Prepared sample count differs from WAV data.");
    var samples = new float[count]; for (int index = 0; index < count; index++) { samples[index] = reader.ReadSingle(); }
    Check(stream.Position == stream.Length, "Prepared WAV has unexpected trailing bytes.");
    return samples;
}
try
{
    var formats = ReferenceAudioFormats.Discover();
    Console.WriteLine("Windows audio extensions: " + string.Join(" ", formats.Extensions));
    var service = new ReferenceAudioService(formats);
    var paths = new List<string>
    {
        await Generate("pcm24-stereo.wav", "pcm_s24le"),
        await Generate("float96.wav", "pcm_f32le", 96000, 1),
        await Generate("lowrate.wav", "pcm_s16le", 8000, 1),
        await Generate("vbr-id3.mp3", "libmp3lame", extra: ["-q:a", "2", "-id3v2_version", "4", "-metadata", "title=ID3 test"]),
        await Generate("cbr-mono.mp3", "libmp3lame", 22050, 1, extra: ["-b:a", "64k"]),
        await Generate("lossless.flac", "flac", 48000, 2),
        await Generate("aac.m4a", "aac", 48000, 2),
        await Generate("adts.aac", "aac", 44100, 1),
        await Generate("windows.wma", "wmav2", 44100, 2),
    };
    paths.Add(await Generate("surround.wav", "pcm_s24le", 48000, 6));
    paths.Add(await Generate("surround.m4a", "aac", 48000, 6));
    paths.Add(await Generate("phase-cancelling.wav", "pcm_f32le", 48000, 2, signal: "aevalsrc=0.25*sin(2*PI*440*t)|-0.25*sin(2*PI*440*t):s=48000:d=0.4"));
    foreach (var format in new (string Name, string Codec, string Container)[] { ("dolby.ac3", "ac3", "ac3"), ("dolby.ec3", "eac3", "eac3"),
        ("mpeg.mpa", "mp2", "mp2"), ("lossless.mka", "flac", "matroska"), ("opus.weba", "libopus", "webm"),
        ("opus.webm", "libopus", "webm"), ("vorbis.webm", "libvorbis", "webm"), ("aac.mp4", "aac", "mp4"),
        ("windows.asf", "wmav2", "asf"), ("aac.3gp", "aac", "3gp"), ("aac.3g2", "aac", "3g2"), ("alac.m4a", "alac", "ipod") })
    {
        if (formats.Extensions.Contains(Path.GetExtension(format.Name)))
        { paths.Add(await Generate(format.Name, format.Codec, 48000, 2, extra: ["-f", format.Container])); }
    }
    // Self-contained AMR-NB SID frames (RFC 4867 storage framing); no external audio or encoder needed.
    string amr = Path.Combine(fixtures, "silence.amr");
    using (var output = File.Create(amr))
    {
        output.Write("#!AMR\n"u8);
        for (int frame = 0; frame < 20; frame++) { output.Write(new byte[] { 0x44, 0, 0, 0, 0, 0 }); }
    }
    if (formats.Extensions.Contains(".amr"))
    {
        paths.Add(amr);
        string disguised = Path.Combine(fixtures, "amr-disguised.bin"); File.Copy(amr, disguised); paths.Add(disguised);
    }
    File.Copy(paths[0], Path.Combine(fixtures, "wave-disguised.mp3")); paths.Add(Path.Combine(fixtures, "wave-disguised.mp3"));
    File.Copy(paths[3], Path.Combine(fixtures, "mp3-disguised.bin")); paths.Add(Path.Combine(fixtures, "mp3-disguised.bin"));
    File.Copy(paths[5], Path.Combine(fixtures, "flac-disguised.wav")); paths.Add(Path.Combine(fixtures, "flac-disguised.wav"));
    foreach (string path in paths)
    {
        using var audio = await service.PrepareAsync(path, default);
        Check(audio.UsesOriginal && audio.InputPath == Path.GetFullPath(path), "Original file was not passed through.");
        var preview = audio.Samples;
        Check(preview.Length > 0 && preview.All(float.IsFinite) && audio.SampleRate > 0, "Decoded samples invalid: " + Path.GetFileName(path));
        Check(Math.Abs(audio.Duration.TotalSeconds - preview.Length / (double)audio.SampleRate) < 0.000001, "Playback duration differs from decoded audio.");
        Check(audio.Waveform.Min(bucket => bucket.Minimum) == preview.Min() && audio.Waveform.Max(bucket => bucket.Maximum) == preview.Max(), "Waveform envelope omits real extrema.");
        Console.WriteLine("PASS decode: " + Path.GetFileName(path)); passed++;
    }
    string longFile = await Generate("long.wav", "pcm_f32le", 48000, 2, 35);
    using (var audio = await service.PrepareAsync(longFile, default))
    {
        Check(Math.Abs(audio.Duration.TotalSeconds - 35) < 0.01 && audio.SampleRate == 48000, "Preview was truncated or resampled to a model-specific rate.");
        var identity = SHA256.HashData(File.ReadAllBytes(longFile));
        try { File.WriteAllText(longFile, "replaced original"); throw new Exception("Loaded source was writable."); }
        catch (IOException) { }
        Check(identity.SequenceEqual(SHA256.HashData(File.ReadAllBytes(audio.InputPath))), "Loaded original changed.");
        string fallback = audio.EnsureFallbackWav(default);
        Check(audio.UsesOriginal && audio.InputPath == longFile, "Creating a fallback prematurely replaced original input.");
        Check(ReadPrepared(fallback, audio.SampleRate).SequenceEqual(audio.Samples), "Fallback trimmed, faded or changed the decoded audio.");
        audio.RuntimeNeedsFallback = true;
        Check(audio.InputPath == fallback, "Decoder failure did not select fallback.");
        try { using var write = File.OpenWrite(fallback); throw new Exception("Fallback PCM was writable."); }
        catch (IOException) { }

    }
    passed++;
    string silence = await Generate("silence.wav", "pcm_s16le", 24000, 1, 0.2, "anullsrc=channel_layout=mono:sample_rate=24000", "-t", "0.2");
    using (var audio = await service.PrepareAsync(silence, default)) { Check(audio.Peak == 0 && audio.Waveform.All(value => value.Minimum == 0 && value.Maximum == 0), "Silence was rejected or fake waveform added."); }
    passed++;

    foreach (string bad in new[] { "broken.mp3", "empty.wav" })
    {
        string path = Path.Combine(fixtures, bad); File.WriteAllBytes(path, bad.StartsWith("empty") ? [] : "not an audio stream"u8.ToArray());
        try { using var audio = await service.PrepareAsync(path, default); throw new Exception("Undecodable input accepted."); }
        catch (InvalidDataException) { passed++; }
    }
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try { using var audio = await service.PrepareAsync(paths[0], cancellation.Token); throw new Exception("Cancelled import accepted."); }
        catch (OperationCanceledException) { passed++; }
    }
    using (var retained = await service.PrepareAsync(paths[0], default))
    {
        var disposable = await service.PrepareAsync(paths[0], default);
        string original = disposable.InputPath, fallback = disposable.EnsureFallbackWav(default);
        disposable.Dispose();
        Check(!disposable.CleanupIncomplete && File.Exists(original) && !File.Exists(fallback) && File.Exists(retained.InputPath), "Cleanup crossed session ownership or left files."); passed++;

    }
    // Headless muted player check: uses the same Windows player and float preview source as the GUI.
    using (var audio = await service.PrepareAsync(paths[0], default))
    using (var source = new ReferenceAudioPlaybackSource(audio.Samples, audio.SampleRate))
    using (var player = new MediaPlayer { AutoPlay = false, IsMuted = true })
    {
        player.CommandManager.IsEnabled = false;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, failure) => { opened.TrySetException(new Exception("Windows player failed: " + failure.ExtendedErrorCode)); ended.TrySetException(new Exception("Windows playback failed.")); };
        player.MediaEnded += (_, _) => ended.TrySetResult();
        player.Source = source.Source; await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(Math.Abs(player.PlaybackSession.NaturalDuration.TotalSeconds - audio.Duration.TotalSeconds) < 0.005, "Player duration differs from waveform.");
        player.Play(); await Task.Delay(80); player.Pause();
        var seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackSession.SeekCompleted += (_, _) => seeked.TrySetResult();
        player.PlaybackSession.Position = TimeSpan.FromSeconds(audio.Duration.TotalSeconds / 2);
        await seeked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Math.Abs(player.PlaybackSession.Position.TotalSeconds - audio.Duration.TotalSeconds / 2) < 0.05, "Seeking did not reach the requested preview position.");
        player.Play(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackSession.Position = TimeSpan.Zero; player.Play(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        player.Pause(); player.Source = null; passed++;
    }
    Console.WriteLine($"PASS: {passed} targeted Windows audio checks. Fixture-only audio; no GUI, TTS inference, model downloads, or live application data changes.");
}
finally
{
    string resolved = Path.GetFullPath(fixtures);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("PocketTTSStudioLite-ReferenceChecks-", StringComparison.Ordinal)) { throw new IOException("Unexpected fixture cleanup path."); }
    Directory.Delete(resolved, recursive: true);
}
