using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketTTSStudioLite.Services;

/// <summary>The official loopback server owns the prepared default voice; generated WAV bytes never leave memory.</summary>
internal sealed class PocketTtsGenerationSession : IAsyncDisposable
{
    internal const int MaximumAudioBytes = 256 * 1024 * 1024;
    private readonly PreparedReferenceAudio _reference;
    private readonly string? _modelIdentity;
    private readonly string _alias;
    private string? _runtimeIdentity;
    private FileStream? _modelLease, _runtimeLease;
    private IReadOnlyList<FileStream> _assets = [];
    private ReferenceAudioStore? _storage;
    private PocketTtsNativeOperation? _native;
    private PocketTtsNativeOperation.Running? _running;
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask;
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PocketTtsExportOutput _diagnostics = new(null);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, UseProxy = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private bool _disposed;

    private PocketTtsGenerationSession(PreparedReferenceAudio reference, PocketTtsInstalledModel model)
    { _reference = reference; _modelIdentity = model.InventoryIdentity; _alias = model.Name; }

    internal bool Matches(PreparedReferenceAudio reference, PocketTtsInstalledModel model) =>
        !_disposed && ReferenceEquals(reference, _reference) && model.Name == _alias && model.InventoryIdentity == _modelIdentity;

    internal static Task<PocketTtsGenerationSession> StartAsync(PreparedReferenceAudio reference, PocketTtsInstalledModel model,
        IProgress<string>? progress, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        for (int attempt = 0; ; attempt++)
        {
            var session = new PocketTtsGenerationSession(reference, model);
            try { await session.StartCoreAsync(model, progress, cancellationToken).ConfigureAwait(false); return session; }
            catch (Exception exception)
            {
                bool retry = attempt == 0 && reference.UsesOriginal && exception is ReferenceAudioException;
                await session.DisposeAsync().ConfigureAwait(false);
                if (!retry) { throw; }
                reference.EnsureFallbackWav(cancellationToken);
                reference.RuntimeNeedsFallback = true;
                progress?.Report("The runtime could not decode the original reference. Retrying with the existing Windows-decoded reference WAV fallback…");
            }
        }
    }, cancellationToken);

    private async Task StartCoreAsync(PocketTtsInstalledModel model, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Verifying the local runtime and selected model…");
        var runtime = await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(cancellationToken).ConfigureAwait(false);
        _runtimeIdentity = PocketTtsCliService.ReferenceRuntimeIdentity(runtime.Identity);
        if (_reference.RuntimeIdentity is { } previous && previous != _runtimeIdentity)
        { throw new InvalidDataException("The active runtime changed. Reload reference audio before generation."); }
        _reference.RuntimeIdentity = _runtimeIdentity;
        _runtimeLease = File.OpenRead(runtime.ExecutablePath);
        await PocketTtsModelSetupService.CheckRuntimeIdentityAsync(runtime.ExecutablePath, cancellationToken).ConfigureAwait(false);
        var cli = new PocketTtsCliService(runtime.ExecutablePath);
        var aliases = await PocketTtsModelSetupService.ValidateCliAsync(cli,
            !PocketTtsRuntimeStore.IsWithin(runtime.ExecutablePath, PocketTtsRuntimeStore.RuntimesPath), cancellationToken).ConfigureAwait(false);
        if (!aliases.Contains(model.Name, StringComparer.Ordinal))
        { throw new InvalidDataException("The selected model is not compatible with the active runtime."); }
        var help = await cli.GetGenerationServerHelpAsync(cancellationToken).ConfigureAwait(false);
        if (!help.Succeeded || new[] { "--config", "--default-voice", "--host", "--port" }.Any(option => !help.StandardOutput.Contains(option, StringComparison.Ordinal)))
        { throw new InvalidDataException("This verified runtime does not expose the supported local generation-server interface."); }
        _modelLease = await PocketTtsModelStore.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
        await VerifyRegistrationAsync(cancellationToken).ConfigureAwait(false);
        _assets = await model.OpenVerifiedAssetsAsync(cancellationToken).ConfigureAwait(false);
        _storage = new ReferenceAudioStore();
        await _storage.WriteGenerationConfigurationAsync(model.LocalConfiguration(), cancellationToken).ConfigureAwait(false);
        _native = new PocketTtsNativeOperation();
        progress?.Report("Loading the model and preparing the reference voice. This can take several minutes…");
        _running = _native.Start(StartInfo(runtime.ExecutablePath, _storage.GenerationConfigurationPath, _reference.InputPath));
        _stdout = ReadDiagnosticsAsync(_running.Output);
        _stderr = ReadDiagnosticsAsync(_running.Error);
        var exit = _running.Process.WaitForExitAsync(cancellationToken);
        var ready = await Task.WhenAny(_endpoint.Task, exit).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (ready != _endpoint.Task)
        {
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (_diagnostics.Failure == PocketTtsVoiceExportFailure.Audio) { throw new ReferenceAudioException(); }
            throw new InvalidOperationException("The local generation runtime could not prepare the selected model and reference. Check their compatibility and available memory.");
        }
        _http.BaseAddress = await _endpoint.Task.ConfigureAwait(false);
        using var healthy = await _http.GetAsync("health", cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        healthy.EnsureSuccessStatusCode();
    }

    internal static ProcessStartInfo StartInfo(string executable, string config, string reference)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "serve", "--config", config, "--default-voice", reference, "--host", "127.0.0.1", "--port", "0" })
        { start.ArgumentList.Add(argument); }
        PocketTtsCliService.ConfigurePrivateModelEnvironment(start);
        return start;
    }

    internal static Uri? EndpointFromDiagnostic(string text)
    {
        var match = Regex.Match(text, @"Server running on http://127\.0\.0\.1:(?<port>[0-9]+)(?=[\s\x1b])");
        return match.Success && int.TryParse(match.Groups["port"].Value, out int port) && port is > 0 and <= 65535
            ? new Uri($"http://127.0.0.1:{port}/") : null;
    }

    private async Task ReadDiagnosticsAsync(StreamReader reader)
    {
        try
        {
            // Consume bounded chunks; never retain native text, prompt text, paths, or credential-bearing logs.
            char[] buffer = new char[2048]; string tail = ""; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                string text = tail + new string(buffer, 0, count);
                _diagnostics.Observe(text);
                if (EndpointFromDiagnostic(text) is { } endpoint) { _endpoint.TrySetResult(endpoint); }
                tail = text[^Math.Min(512, text.Length)..];
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private async Task VerifyRegistrationAsync(CancellationToken cancellationToken)
    {
        var record = (await new PocketTtsModelInventoryService().ReadAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(entry => entry.Alias == _alias);
        if (record is null || _modelIdentity is null || PocketTtsModelInventoryService.GetIdentity(record) != _modelIdentity)
        { throw new InvalidDataException("The selected model was removed or changed. Refresh models before generation."); }
    }

    internal async Task<AudioPlaybackData> GenerateAsync(string text, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _requestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        { throw new InvalidOperationException("A generation is already running."); }
        try
        {
            var runtime = await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(cancellationToken).ConfigureAwait(false);
            if (_runtimeIdentity != PocketTtsCliService.ReferenceRuntimeIdentity(runtime.Identity))
            { throw new InvalidDataException("The active runtime changed. Reload the reference before generating again."); }
            await VerifyRegistrationAsync(cancellationToken).ConfigureAwait(false);
            if (_running is null || _running.Process.HasExited) { throw new InvalidOperationException("The local generation runtime stopped. Generate again to restart it."); }
            progress?.Report("Generating speech locally using the prepared voice…");
            byte[] audio = await RequestAudioAsync(_http, text, cancellationToken).ConfigureAwait(false);
            progress?.Report("Decoding generated audio in memory…");
            var decoded = await AudioPlaybackData.DecodeMemoryAsync(audio, cancellationToken).ConfigureAwait(false);
            var currentRuntime = await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(cancellationToken).ConfigureAwait(false);
            if (_runtimeIdentity != PocketTtsCliService.ReferenceRuntimeIdentity(currentRuntime.Identity))
            { throw new InvalidDataException("The runtime changed during generation. Reload the reference and generate again."); }
            await VerifyRegistrationAsync(cancellationToken).ConfigureAwait(false);
            return decoded;
        }
        finally { _requestGate.Release(); }
    }

    internal static async Task<byte[]> RequestAudioAsync(HttpClient client, string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "tts") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { throw new InvalidDataException("The runtime could not generate this text. Check the selected model and reference, then try again."); }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAudioAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadAudioAsync(Stream stream, CancellationToken cancellationToken, int maximumBytes = MaximumAudioBytes)
    {
        if (maximumBytes <= 0 || maximumBytes > MaximumAudioBytes) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
        using var audio = new MemoryStream();
        byte[] buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (audio.Length + count > maximumBytes) { throw new InvalidDataException("The generated preview exceeds the audio memory limit. Use a shorter text."); }
            await audio.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (audio.Length == 0) { throw new InvalidDataException("The runtime returned no generated audio."); }
        return audio.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true; _http.Dispose();
        Exception? failure = null;
        try { if (_native is not null) { await _native.DisposeAsync().ConfigureAwait(false); } }
        catch (Exception exception) { failure = exception; }
        try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        try { _running?.Dispose(); }
        catch (Exception exception) { failure ??= exception; }
        foreach (var asset in _assets) { asset.Dispose(); }
        _modelLease?.Dispose(); _runtimeLease?.Dispose();
        if (failure is not null) { _reference.PreserveTemporaryFiles = true; if (_storage is not null) { _storage.PreserveFiles = true; } }
        _storage?.Dispose();
        if (failure is not null || _storage?.CleanupIncomplete == true)
        { throw new PocketTtsGenerationCleanupException("Generation cleanup could not be confirmed. Uncertain native-operation metadata and temporary reference/configuration files were retained.", failure); }
    }

    private sealed class ReferenceAudioException : Exception;
}

internal sealed class PocketTtsGenerationCleanupException(string message, Exception? inner) : InvalidOperationException(message, inner);
