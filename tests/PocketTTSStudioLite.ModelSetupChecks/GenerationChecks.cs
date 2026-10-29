using PocketTTSStudioLite.Services;
using System.Runtime.InteropServices;
using System.Xml.Linq;

internal static class GenerationChecks
{
    internal static async Task RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        string[] valid = ["Server running on http://127.0.0.1:43210\n", "INFO Server running on http://127.0.0.1:1\u001b[0m\r\n"];
        foreach (string log in valid)
        { Check(PocketTtsGenerationSession.EndpointFromDiagnostic(log) is { IsLoopback: true, Scheme: "http" }, "A complete loopback server address was not recognized."); }
        foreach (string log in new[] { "Server running on http://127.0.0.1:432", "Server running on http://127.0.0.1:0\n", "Server running on http://127.0.0.1:70000\n",
            "Server running on http://example.com:1234\n", "Server running on http://127.0.0.1:1234.evil\n", "Server running on https://127.0.0.1:1234\n" })
        { Check(PocketTtsGenerationSession.EndpointFromDiagnostic(log) is null, "A partial, remote, invalid, or unexpected endpoint was accepted."); }
        var start = PocketTtsGenerationSession.StartInfo(@"C:\runtime\pocket-tts.exe", @"C:\private model\config.yaml", @"C:\audio\reference.wav");
        Check(start.ArgumentList.SequenceEqual(new[] { "serve", "--config", @"C:\private model\config.yaml", "--default-voice", @"C:\audio\reference.wav", "--host", "127.0.0.1", "--port", "0" }),
            "The generation session does not use the official local default-voice interface.");
        Check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardOutput && start.RedirectStandardError, "The native server is not hidden with drainable output streams.");
        Check(!start.Environment.ContainsKey("HF_TOKEN") && !start.Environment.ContainsKey("HF_HOME") && start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] == "1",
            "Generation inherited ambient credentials/cache selection.");
        var environment = new System.Diagnostics.ProcessStartInfo();
        environment.Environment.Clear();
        string[] privateVariables = ["HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "HF_ASSETS_CACHE", "HF_XET_CACHE", "XDG_CACHE_HOME",
            "TRANSFORMERS_CACHE", "PYTORCH_TRANSFORMERS_CACHE", "PYTORCH_PRETRAINED_BERT_CACHE", "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"];
        foreach (string variable in privateVariables) { environment.Environment[variable] = "synthetic-test-value"; }
        environment.Environment["HOME"] = @"C:\existing-home";
        PocketTtsCliService.ConfigurePrivateModelEnvironment(environment);
        Check(privateVariables.All(variable => !environment.Environment.ContainsKey(variable)) &&
            environment.Environment["HOME"] == @"C:\existing-home" && environment.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] == "1",
            "The shared export/generation environment policy changed or discarded an existing HOME.");
        environment.Environment["HOME"] = " ";
        PocketTtsCliService.ConfigurePrivateModelEnvironment(environment);
        Check(environment.Environment["HOME"] == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "A blank native HOME was not initialized consistently.");
        using (var bytes = new MemoryStream([1, 2, 3, 4]))
        { Check((await PocketTtsGenerationSession.ReadAudioAsync(bytes, default)).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "The complete in-memory response was modified."); }
        using (var empty = new MemoryStream())
        {
            try { await PocketTtsGenerationSession.ReadAudioAsync(empty, default); throw new Exception("Empty generation succeeded."); }
            catch (InvalidDataException) { passed++; }
        }
        using (var large = new MemoryStream(new byte[9]))
        {
            try { await PocketTtsGenerationSession.ReadAudioAsync(large, default, maximumBytes: 8); throw new Exception("Oversized generation bypassed its memory bound."); }
            catch (InvalidDataException) { passed++; }
        }
        using (var cancellation = new CancellationTokenSource())
        using (var bytes = new MemoryStream(new byte[16]))
        {
            cancellation.Cancel();
            try { await PocketTtsGenerationSession.ReadAudioAsync(bytes, cancellation.Token); throw new Exception("A cancelled response became a preview."); }
            catch (OperationCanceledException) { passed++; }
        }
        using (var broken = new BrokenResponse())
        {
            try { await PocketTtsGenerationSession.ReadAudioAsync(broken, default); throw new Exception("An incomplete response became a preview."); }
            catch (IOException) { passed++; }
        }

        byte[] Wave(bool streamingHeader)
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory);
            const int frames = 2400;
            int bytes = streamingHeader ? 2_000_000_000 : frames * 2;
            writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000);
            writer.Write(48000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
            for (int index = 0; index < frames; index++) { writer.Write((short)(Math.Sin(index * Math.PI / 30) * 8000)); }
            writer.Flush(); return memory.ToArray();
        }
        var audio = await AudioPlaybackData.DecodeMemoryAsync(Wave(false), default);
        Check(audio.Samples.Length == 2400 && audio.SampleRate == 24000 && audio.Duration == TimeSpan.FromSeconds(0.1), "Memory playback position/duration data differs from actual decoded samples.");
        Check(audio.Waveform.Min(bin => bin.Minimum) == audio.Samples.Min() && audio.Waveform.Max(bin => bin.Maximum) == audio.Samples.Max(),
            "The shared memory waveform lost actual sample extrema.");
        var streamed = await AudioPlaybackData.DecodeMemoryAsync(Wave(true), default);
        Check(streamed.Samples.SequenceEqual(audio.Samples) && streamed.Duration == audio.Duration,
            "The official runtime's unseekable WAV header was not decoded using the actual received sample count.");
        const string prompt = "Adventurer—listen & answer + tell me: žluťoučký?";
        using (var client = new HttpClient(new GenerationResponse(Wave(true), async request =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri is { IsLoopback: true, AbsolutePath: "/tts" }, "Generation did not use the official loopback POST endpoint.");
            string form = await request.Content!.ReadAsStringAsync();
            Check(form.StartsWith("text=", StringComparison.Ordinal) && !form.Contains('&') &&
                Uri.UnescapeDataString(form[5..].Replace('+', ' ')) == prompt,
                "The prompt changed or extra voice/file/upload fields bypassed reuse of the prepared default voice.");
        })))
        {
            client.BaseAddress = new Uri("http://127.0.0.1:12345/");
            byte[] response = await PocketTtsGenerationSession.RequestAudioAsync(client, prompt, default);
            Check(response.SequenceEqual(Wave(true)), "Generated audio bytes were not received intact in memory.");
        }
        using (var client = new HttpClient(new GenerationResponse([], _ => Task.CompletedTask, System.Net.HttpStatusCode.InternalServerError)))
        {
            client.BaseAddress = new Uri("http://127.0.0.1:12345/");
            try { await PocketTtsGenerationSession.RequestAudioAsync(client, prompt, default); throw new Exception("A failed runtime response became a preview."); }
            catch (InvalidDataException) { passed++; }
        }
        try { await AudioPlaybackData.DecodeMemoryAsync("not audio"u8.ToArray(), default); throw new Exception("Invalid generated audio became a preview."); }
        catch (Exception exception) when (exception is InvalidDataException or COMException) { passed++; }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await AudioPlaybackData.DecodeMemoryAsync(Wave(false), cancellation.Token); throw new Exception("Cancelled decoding succeeded."); }
            catch (OperationCanceledException) { passed++; }
        }
        Check(audio.Samples.Length == 2400 && audio.Waveform.Count > 0, "Failed/cancelled decoding mutated the previous audio.");

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var document = XDocument.Load(Path.Combine(root, "PocketTTSStudioLite", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var players = document.Descendants().Where(element => element.Name.LocalName == "AudioPlayerControl").ToArray();
        Check(players.Length == 2 && players.Select(element => (string?)element.Attribute(x + "Name")).ToHashSet().SetEquals(new[] { "ReferencePlayer", "VoicePreviewPlayer" }),
            "Reference and preview do not share exactly one player control implementation.");
        Check(!document.Descendants().Any(element => element.Name.LocalName is "Canvas" or "Path" or "Line"), "A second waveform implementation remains embedded in the page.");
        Check(document.Descendants().Any(element => (string?)element.Attribute(x + "Name") == "GenerateButton" && (string?)element.Attribute("Click") == "Generate_Click"),
            "Generate is not wired to its guarded asynchronous handler.");
        var control = XDocument.Load(Path.Combine(root, "PocketTTSStudioLite", "AudioPlayerControl.xaml"));
        Check((string?)control.Root!.Attribute("Visibility") == "Collapsed", "An empty shared player reserves space before audio is loaded.");
        Check(!control.Descendants().Any(element => (string?)element.Attribute(x + "Name") == "ReferenceWaveformPlaceholder") &&
            !players.Any(element => element.Attribute("EmptyMessage") is not null) &&
            !document.Descendants().Any(element => (string?)element.Attribute("Text") == "Preview playback is not available yet."),
            "An obsolete empty-player placeholder or preview status remains.");
        Check(control.Descendants().Any(element => (string?)element.Attribute("PointerPressed") == "ReferenceWaveform_PointerPressed") &&
            control.Descendants().Any(element => (string?)element.Attribute("Click") == "ReferencePlayPause_Click"), "The shared player lost waveform seeking or playback.");
        Console.WriteLine($"PASS: {passed} targeted generation/memory-preview checks. Native server arguments, bounded response handling, memory MF decoding, real waveform/duration, and shared-control structure; no GUI, model inference, or generated audio files.");
    }

    private sealed class BrokenResponse : MemoryStream
    {
        private bool _sent;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent) { throw new IOException("Synthetic interrupted HTTP body."); }
            _sent = true; buffer.Span[0] = 1; return ValueTask.FromResult(1);
        }
    }

    private sealed class GenerationResponse(byte[] bytes, Func<HttpRequestMessage, Task> inspect,
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await inspect(request);
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        }
    }
}
