using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PocketTTSStudioLite.Services;

const string FixtureBytes = "SYNTHETIC TEST OUTPUT - NOT A VOICE PROFILE";
const string DiagnosticSecret = "fixture-diagnostic-value-never-display";
if (args is ["fixture-idle"]) { await Task.Delay(Timeout.InfiniteTimeSpan); return; }
if (args is ["--version"])
{
    if (Environment.GetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION") == "1")
    { await Task.Delay(Timeout.InfiniteTimeSpan); }
    Console.WriteLine("pocket-tts 3.3.0");
    return;
}
if (args is ["export-voice", "--help"])
{
    // Alias discovery still reads official help; export invocations accept only --config.
    Console.WriteLine("Usage: export-voice <AUDIO_PATH> <EXPORT_PATH>\n--config <CONFIG>\n--language 'english_2026-10' Incompatible with --config");
    return;
}
if (args is ["export-voice", "--config", var configuration, var input, var output])
{
    if (!File.Exists(input) || !Path.IsPathFullyQualified(input) || !Path.IsPathFullyQualified(output) || !File.Exists(configuration))
    { Environment.ExitCode = 91; return; }
    string yaml = await File.ReadAllTextAsync(configuration);
    string privateRoot = Environment.GetEnvironmentVariable("POCKET_TTS_TEST_PRIVATE_ROOT")!;
    var paths = Regex.Matches(yaml, @"(?m)^\s*(?:weights_path|tokenizer_path):\s*'(?<path>[^\r\n]*)'\s*$");
    if (paths.Count != 2 || yaml.Contains("hf://") || yaml.Contains("https://") ||
        paths.Any(match => !File.Exists(match.Groups["path"].Value.Replace("''", "'")) ||
            !PocketTtsRuntimeStore.IsWithin(match.Groups["path"].Value.Replace("''", "'"), Path.Combine(privateRoot, "models", "blobs"))))
    { Environment.ExitCode = 92; return; }
    // Validate environment isolation without printing credential values.
    string[] forbidden = ["HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE",
        "HF_ASSETS_CACHE", "HF_XET_CACHE", "XDG_CACHE_HOME", "TRANSFORMERS_CACHE", "PYTORCH_TRANSFORMERS_CACHE", "PYTORCH_PRETRAINED_BERT_CACHE"];
    if (forbidden.Any(variable => Environment.GetEnvironmentVariable(variable) is not null) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOME")) || Environment.GetEnvironmentVariable("HF_HUB_DISABLE_IMPLICIT_TOKEN") != "1")
    { Environment.ExitCode = 93; return; }
    await Console.Out.WriteAsync("INFO: Loading TTSModel weights from fixture. " + DiagnosticSecret);
    await Console.Error.WriteAsync("fixture raw diagnostic: " + DiagnosticSecret);
    string? mode = Environment.GetEnvironmentVariable("POCKET_TTS_TEST_EXPORT_MODE");
    if (mode is "cancel" or "partial")
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("fixture-idle");
        using Process child = Process.Start(start)!;
        await PublishPidsAsync(Environment.ProcessId + " " + child.Id);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return;
    }
    if (mode == "long")
    {
        await PublishPidsAsync(Environment.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromSeconds(17));
    }
    if (mode is "access" or "network" or "audio")
    {
        if (mode == "audio") { await Console.Out.WriteAsync("INFO: Loading tokenizers json @00eac05ed3d16bdc3f6b5d598874019c34a89214"); }
        await Console.Error.WriteAsync(mode switch
        {
            "access" => "ERROR: We could not download the weights for the model with voice cloning. Authentication required (401).",
            "network" => "ERROR: error sending request: connection failed",
            _ => "ERROR: unsupported audio codec",
        });
        Environment.ExitCode = 31;
        return;
    }
    if (mode != "empty") { await File.WriteAllTextAsync(output, FixtureBytes); }
    if (mode == "race")
    { await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("POCKET_TTS_TEST_DESTINATION")!, "competing-data"); }
    return;
}
if (args.Length != 0) { throw new Exception("The export fixture received unsupported arguments."); }

string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Pocket TTS export checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
string audio = Path.Combine(temporaryDirectory, "reference with spaces and ž.wav");
using (var writer = new BinaryWriter(File.Create(audio)))
{
    const int frames = 2400, rate = 24000;
    writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
    writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 2);
    for (int index = 0; index < frames; index++) { writer.Write((short)(Math.Sin(index * Math.PI / 30) * 8000)); }
}
string[] variableNames = ["HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "HF_ASSETS_CACHE", "HF_XET_CACHE",
    "XDG_CACHE_HOME", "TRANSFORMERS_CACHE", "PYTORCH_TRANSFORMERS_CACHE", "PYTORCH_PRETRAINED_BERT_CACHE", "HOME", "HF_HUB_DISABLE_IMPLICIT_TOKEN",
    "POCKET_TTS_TEST_PRIVATE_ROOT", "POCKET_TTS_TEST_HANG_VERSION", "POCKET_TTS_TEST_EXPORT_MODE", "POCKET_TTS_TEST_PIDS", "POCKET_TTS_TEST_DESTINATION"];
var originalEnvironment = variableNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
int passed = 0;
try
{
    await PocketTtsRuntimeStore.InitializeAsync(Path.Combine(temporaryDirectory, "app-data"), Environment.ProcessPath!);
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_PRIVATE_ROOT", PocketTtsRuntimeStore.LocalDataPath);
    foreach (string variable in variableNames.Where(name => name.StartsWith("HF_", StringComparison.Ordinal) || name == "HUGGING_FACE_HUB_TOKEN"))
    { Environment.SetEnvironmentVariable(variable, null); }
    Environment.SetEnvironmentVariable("HOME", null);
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", null);
    var service = new PocketTtsCliService(Environment.ProcessPath!);
    var formats = new ReferenceAudioFormats([new ReferenceAudioFormats.Route("audio/wav", [".wav"])]);
    var audioService = new ReferenceAudioService(formats);
    var model = await CreatePrivateModelAsync();

    async Task<PocketTtsVoiceExportResult> ExportAsync(string input, string destination, IProgress<PocketTtsVoiceExportProgress>? progress = null,
        CancellationToken cancellationToken = default, PocketTtsCliService? runner = null, bool overwriteConfirmed = false)
    {
        using var reference = await audioService.PrepareAsync(input, cancellationToken);
        return await (runner ?? service).ExportPreparedReferenceAsync(reference, destination, model, progress, cancellationToken, overwriteConfirmed);
    }

    await Verify("Private inventory, prepared audio, local-config arguments, and unchanged synthetic output", async () =>
    {
        SetMode("success");
        string destination = PocketTtsVoiceProfileFile.Destination(temporaryDirectory, audio, model.Name);
        PocketTtsVoiceExportResult result = await ExportAsync(audio, destination);
        Check(result.Succeeded && result.ExitCode == 0 && await File.ReadAllTextAsync(destination) == FixtureBytes,
            "Prepared-reference export altered native bytes, failed its private config contract, or required authentication.");
    });

    await Verify("Ambient credentials/cache selectors are stripped and raw native logs never enter results/progress", async () =>
    {
        SetMode("success");
        Environment.SetEnvironmentVariable("HF_TOKEN", "fixture-ambient-token");
        Environment.SetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN", "fixture-other-token");
        Environment.SetEnvironmentVariable("HF_HOME", "fixture-ambient-cache-selector");
        Environment.SetEnvironmentVariable("HF_HUB_DISABLE_IMPLICIT_TOKEN", "0");
        var progress = new CapturedProgress();
        try
        {
            PocketTtsVoiceExportResult result = await ExportAsync(audio, Destination(), progress);
            Check(result.Succeeded && !JsonSerializer.Serialize(result).Contains(DiagnosticSecret) &&
                progress.Messages.All(message => !message.Contains(DiagnosticSecret)), "Raw native diagnostics reached a result/progress value.");
            Check(Environment.GetEnvironmentVariable("HF_TOKEN") == "fixture-ambient-token" &&
                Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN") == "fixture-other-token" &&
                Environment.GetEnvironmentVariable("HF_HOME") == "fixture-ambient-cache-selector" &&
                Environment.GetEnvironmentVariable("HF_HUB_DISABLE_IMPLICIT_TOKEN") == "0", "Parent environment changed.");
        }
        finally
        {
            foreach (string variable in new[] { "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HF_HOME", "HF_HUB_DISABLE_IMPLICIT_TOKEN" })
            { Environment.SetEnvironmentVariable(variable, null); }
        }
    });

    await Verify("Native access, network, and audio failures have safe results; audio fallback cannot publish failure", async () =>
    {
        foreach (var (mode, failure) in new[] { ("access", PocketTtsVoiceExportFailure.ModelAccessOrCache),
            ("network", PocketTtsVoiceExportFailure.Connectivity), ("audio", PocketTtsVoiceExportFailure.Audio) })
        {
            SetMode(mode);
            string destination = Destination();
            var progress = new CapturedProgress();
            PocketTtsVoiceExportResult result = await ExportAsync(audio, destination, progress);
            Check(!result.Succeeded && result.ExitCode == 31 && result.Failure == failure && !File.Exists(destination), "Incorrect runtime error result.");
            Check(result.AuthenticationMayBeRequired == (mode == "access"), "Access-error classification changed.");
            if (mode == "audio") { Check(progress.Messages.Any(message => message.Contains("Retrying")), "The prepared-reference audio fallback was not attempted."); }
        }
    });

    await Verify("Invalid references, existing destinations, and empty native output are preserved/rejected", async () =>
    {
        SetMode("success");
        await Expect<ArgumentException>(ExportAsync("https://example.com/audio.wav", Destination()));
        string profile = Destination();
        await File.WriteAllTextAsync(profile, "existing-user-profile");
        await Expect<InvalidDataException>(ExportAsync(profile, Destination()));
        await Expect<IOException>(ExportAsync(audio, profile));
        Check(await File.ReadAllTextAsync(profile) == "existing-user-profile", "An invalid input or unconfirmed overwrite changed the existing profile.");
        await Expect<FileNotFoundException>(ExportAsync(Path.Combine(temporaryDirectory, "missing.wav"), Destination()));
        SetMode("empty");
        string destination = Destination();
        PocketTtsVoiceExportResult result = await ExportAsync(audio, destination);
        Check(!result.Succeeded && !File.Exists(destination), "Empty native output was published.");
        result = await ExportAsync(audio, profile, overwriteConfirmed: true);
        Check(!result.Succeeded && await File.ReadAllTextAsync(profile) == "existing-user-profile", "Failed confirmed replacement damaged the original.");
    });

    await Verify("A destination created during export is never overwritten", async () =>
    {
        SetMode("race");
        string destination = Destination();
        Environment.SetEnvironmentVariable("POCKET_TTS_TEST_DESTINATION", destination);
        await Expect<IOException>(ExportAsync(audio, destination));
        Check(await File.ReadAllTextAsync(destination) == "competing-data", "A concurrently created destination was overwritten.");
    });

    await Verify("Cancellation and partial tree errors preserve outputs; native job containment stops descendants", async () =>
    {
        foreach (bool partial in new[] { false, true })
        {
            SetMode(partial ? "partial" : "cancel");
            string pidPath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".pids");
            Environment.SetEnvironmentVariable("POCKET_TTS_TEST_PIDS", pidPath);
            string destination = Destination();
            if (partial) { await File.WriteAllTextAsync(destination, "existing-user-profile"); }
            var partialFailure = new AggregateException(new InvalidOperationException("Fixture descendant failure."));
            var runner = partial ? new PocketTtsCliService(Environment.ProcessPath!, _ => throw partialFailure) : service;
            using var cancellation = new CancellationTokenSource();
            Task<PocketTtsVoiceExportResult> operation = ExportAsync(audio, destination, cancellationToken: cancellation.Token, runner: runner, overwriteConfirmed: partial);
            Process? parent = null;
            Process? child = null;
            try
            {
                await WaitForFileAsync(pidPath);
                string[] ids = (await File.ReadAllTextAsync(pidPath)).Split(' ');
                parent = Process.GetProcessById(int.Parse(ids[0]));
                child = Process.GetProcessById(int.Parse(ids[1]));
                cancellation.Cancel();
                if (partial)
                {
                    var error = await Expect<InvalidOperationException>(operation);
                    Check(ReferenceEquals(error.InnerException, partialFailure), "Partial process-tree termination failure was hidden.");
                }
                else
                {
                    var error = await Expect<OperationCanceledException>(operation);
                    Check(error.CancellationToken == cancellation.Token, "Caller token changed.");
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(partial ? await File.ReadAllTextAsync(destination) == "existing-user-profile" : !File.Exists(destination),
                    "Cancelled/failed export published or corrupted a destination.");
            }
            finally
            {
                cancellation.Cancel();
                try { await operation.WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
                if (parent is not null) { await StopFixture(parent); parent.Dispose(); }
                if (child is not null) { await StopFixture(child); child.Dispose(); }
            }
        }
    });

    await Verify("Exports exceed 15 seconds while diagnostics retain their 15-second timeout", async () =>
    {
        SetMode("long");
        string started = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".pids");
        Environment.SetEnvironmentVariable("POCKET_TTS_TEST_PIDS", started);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var elapsed = Stopwatch.StartNew();
        Task<PocketTtsVoiceExportResult> export = ExportAsync(audio, Destination(), cancellationToken: deadline.Token);
        try
        {
            // Finish export preflight before selecting a hanging diagnostic child.
            await WaitForFileAsync(started);
            Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", "1");
            Task<TimeoutException> diagnostic = Expect<TimeoutException>(service.GetVersionAsync(), TimeSpan.FromSeconds(25));
            Check((await export).Succeeded && elapsed.Elapsed >= TimeSpan.FromSeconds(16), "Export received a diagnostic timeout.");
            Check((await diagnostic).Message.Contains("15-second"), "Diagnostic deadline changed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", null);
            deadline.Cancel();
            try { await export.WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
        }
    });

    await Verify("Changed private registration or weights cannot be exported", async () =>
    {
        SetMode("success");
        string inventory = PocketTtsModelInventoryService.InventoryPath;
        string saved = await File.ReadAllTextAsync(inventory);
        try
        {
            await File.WriteAllTextAsync(inventory, "{\"SchemaVersion\":2,\"Models\":[]}");
            await Expect<InvalidDataException>(ExportAsync(audio, Destination()));
        }
        finally { await File.WriteAllTextAsync(inventory, saved); }
        string weights = model.Assets.Single(asset => asset.Weights).Path;
        byte[] original = await File.ReadAllBytesAsync(weights);
        try
        {
            await File.AppendAllTextAsync(weights, "tampered");
            await Expect<InvalidDataException>(ExportAsync(audio, Destination()));
        }
        finally { await File.WriteAllBytesAsync(weights, original); }
    });

    await Verify("Validated runtime diagnostics still work without invoking real model/export operations", async () =>
    {
        Check((await service.GetVersionAsync()).StandardOutput.Trim() == "pocket-tts 3.3.0", "Unexpected fixture runtime version.");
        Check(PocketTtsModelSetupService.SupportedLanguages((await service.GetExportVoiceHelpAsync()).StandardOutput).Contains(model.Name),
            "Fixture help no longer exposes the current model-alias discovery contract.");
        string runtime = Path.Combine(FindRepositoryRoot(), "development", "runtime", "pocket-tts", "v4.0.1", "windows-x64", "extracted",
            "pocket-tts-3.3.0-x86_64-pc-windows-msvc", "pocket-tts.exe");
        if (!File.Exists(runtime)) { Console.WriteLine("SKIP optional real runtime diagnostics: no local pinned executable."); return; }
        using (var bytes = File.OpenRead(runtime))
        { Check(Convert.ToHexString(await SHA256.HashDataAsync(bytes)) == PocketTtsModelSetupService.ValidatedRuntimeSha256, "Unexpected real runtime hash."); }
        var real = new PocketTtsCliService(runtime);
        Check((await real.GetVersionAsync()).StandardOutput.Trim() == "pocket-tts 3.3.0", "Unexpected real runtime version.");
        Check((await real.GetExportVoiceHelpAsync()).StandardOutput.Contains("english_2026-10"), "Validated English model is absent from CLI help.");
    });
    Console.WriteLine($"{passed} targeted private-model/prepared-reference export check groups passed; no real model download or voice export was run.");
}
finally
{
    foreach (var entry in originalEnvironment) { Environment.SetEnvironmentVariable(entry.Key, entry.Value); }
    Check(string.Equals(Path.GetDirectoryName(Path.GetFullPath(temporaryDirectory))?.TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
        "Refusing cleanup outside the owned temporary directory root.");
    Directory.Delete(temporaryDirectory, recursive: true);
}

string Destination() => Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".safetensors");
void SetMode(string mode) => Environment.SetEnvironmentVariable("POCKET_TTS_TEST_EXPORT_MODE", mode);
async Task Verify(string name, Func<Task> test)
{
    await test();
    Check(Directory.GetFiles(temporaryDirectory, ".PocketTTSStudioLite-*.safetensors").Length == 0, "Owned staging files were left behind.");
    Check(!Directory.EnumerateFiles(Path.Combine(PocketTtsModelStore.Root, ".native-operations")).Any(), "Native operation markers were left behind.");
    passed++;
    Console.WriteLine("PASS " + name);
}
static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
static async Task<T> Expect<T>(Task operation, TimeSpan? timeout = null) where T : Exception
{
    try { await operation.WaitAsync(timeout ?? TimeSpan.FromSeconds(15)); }
    catch (T exception) { return exception; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static async Task StopFixture(Process process)
{
    if (!process.HasExited) { process.Kill(entireProcessTree: true); }
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
static async Task WaitForFileAsync(string path)
{
    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!File.Exists(path)) { await Task.Delay(10, wait.Token); }
}
static async Task PublishPidsAsync(string ids)
{
    string path = Environment.GetEnvironmentVariable("POCKET_TTS_TEST_PIDS")!;
    await File.WriteAllTextAsync(path + ".tmp", ids);
    File.Move(path + ".tmp", path);
}
static string FindRepositoryRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    { if (File.Exists(Path.Combine(directory.FullName, "PocketTTSStudioLite", "PocketTTSStudioLite.csproj"))) { return directory.FullName; } }
    throw new DirectoryNotFoundException("Build these checks inside this checkout.");
}
static async Task<PocketTtsInstalledModel> CreatePrivateModelAsync()
{
    const string repository = "kyutai/pocket-tts";
    string revision = new('a', 40);
    var files = new List<PocketTtsModelInventoryFile>();
    using var memory = new MemoryStream();
    using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
    {
        byte[] header = Encoding.UTF8.GetBytes("{\"mimi.encoder.model.0.conv.weight\":{},\"flow_lm.speaker_proj_weight\":{}}");
        writer.Write((ulong)header.Length); writer.Write(header);
    }
    foreach (var (path, bytes, weights) in new[] { ("model.safetensors", memory.ToArray(), true), ("tokenizer.json", "{\"fixture\":true}"u8.ToArray(), false) })
    {
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        string blob = PocketTtsModelStore.BlobPath(true, digest);
        PocketTtsModelStore.EnsureDirectory(Path.GetDirectoryName(blob)!);
        await File.WriteAllBytesAsync(blob, bytes);
        files.Add(new(Path.GetRelativePath(PocketTtsModelStore.Root, blob), path, bytes.Length, digest, "SHA256", weights));
    }
    string yaml = $"weights_path: hf://{repository}/model.safetensors@{revision}\ntokenizer_path: hf://{repository}/tokenizer.json@{revision}\n";
    var record = new PocketTtsModelInventoryEntry("english_2026-10", repository, revision, PocketTtsModelStore.Root, files, yaml);
    var inventory = new PocketTtsModelInventoryService();
    await inventory.RegisterAfterInstallationAsync(record);
    return PocketTtsModelInventoryService.CreateModel((await inventory.ReadAsync()).Single());
}
sealed class CapturedProgress : IProgress<PocketTtsVoiceExportProgress>
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public void Report(PocketTtsVoiceExportProgress value) => Messages.Enqueue(value.Message);
}

namespace PocketTTSStudioLite.Services
{
    // Same sandbox substitution pattern as the existing removal/reference check projects.
    // Only runtime trust/data location is a fixture; the application is never changed.
    internal static class PocketTtsRuntimeStore
    {
        internal static string LocalDataPath { get; private set; } = "";
        internal static string RuntimesPath => Path.Combine(LocalDataPath, "runtimes");
        private static VerifiedRuntime? _runtime;
        internal sealed record VerifiedRuntime(PocketTtsModelSetupService.RuntimeIdentity Identity, string ExecutablePath);
        internal static async Task InitializeAsync(string directory, string executable)
        {
            LocalDataPath = Path.GetFullPath(directory);
            EnsureDirectory(LocalDataPath);
            using var bytes = File.OpenRead(executable);
            _runtime = new(new("fixture-runtime", Convert.ToHexString(await SHA256.HashDataAsync(bytes))), Path.GetFullPath(executable));
        }
        internal static string ResolveActivePath() => (_runtime ?? throw new InvalidOperationException("Initialize fixture runtime first.")).ExecutablePath;
        internal static Task<VerifiedRuntime> ReadVerifiedActiveAsync(CancellationToken cancellationToken, bool initializeIdentity = false) =>
            VerifyRuntimeAsync(ResolveActivePath(), cancellationToken, initializeIdentity);
        internal static async Task<VerifiedRuntime> VerifyRuntimeAsync(string path, CancellationToken cancellationToken, bool initializeIdentity = true)
        {
            var expected = _runtime ?? throw new InvalidOperationException("Initialize fixture runtime first.");
            if (!string.Equals(Path.GetFullPath(path), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("Only the explicitly authorized fixture executable may export."); }
            using var bytes = File.OpenRead(path);
            if (Convert.ToHexString(await SHA256.HashDataAsync(bytes, cancellationToken)) != expected.Identity.ExecutableSha256)
            { throw new InvalidDataException("Fixture executable changed."); }
            return expected;
        }
        internal static void EnsureDirectory(string path) => Directory.CreateDirectory(path);
        internal static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
    }
}
