using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;

namespace PocketTTSStudioLite.Services;

/// <summary>Runs diagnostics and verified private-model voice export using the official Windows x64 runtime.</summary>
public sealed class PocketTtsCliService
{
    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly string? _executablePath;
    private readonly Action<Process> _terminateProcessTree;

    /// <param name="executablePath">
    /// Optional executable path. Relative paths are based on AppContext.BaseDirectory.
    /// Otherwise use the verified active local runtime, or the pinned app-relative bundled fallback.
    /// </param>
    public PocketTtsCliService(string? executablePath = null)
        : this(executablePath, static process => process.Kill(entireProcessTree: true))
    {
    }

    // Keep termination failures testable without requiring protected OS processes.
    internal PocketTtsCliService(string? executablePath, Action<Process> terminateProcessTree)
    {
        ArgumentNullException.ThrowIfNull(terminateProcessTree);
        if (executablePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        }

        _executablePath = executablePath;
        _terminateProcessTree = terminateProcessTree;
    }

    public Task<PocketTtsCliResult> GetVersionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(["--version"], cancellationToken);

    public Task<PocketTtsCliResult> GetExportVoiceHelpAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(["export-voice", "--help"], cancellationToken);

    internal Task<PocketTtsCliResult> GetGenerationServerHelpAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(["serve", "--help"], cancellationToken);

    internal Task<PocketTtsVoiceExportResult> ExportPreparedReferenceAsync(PreparedReferenceAudio reference, string destinationPath,
        PocketTtsInstalledModel model, IProgress<PocketTtsVoiceExportProgress>? progress, CancellationToken cancellationToken,
        bool overwriteConfirmed = false) => Task.Run(async () =>
    {
        string destination = LocalPath(destinationPath);
        if (!Path.GetExtension(destination).Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("The export destination must end with .safetensors.", nameof(destinationPath)); }
        if (!overwriteConfirmed && File.Exists(destination))
        { throw new IOException("The profile filename already exists. Export again to confirm whether to overwrite it."); }
        string completedPath = Path.Combine(Path.GetDirectoryName(destination)!, $".PocketTTSStudioLite-ready-{Guid.NewGuid():N}.safetensors");
        Exception? operationFailure = null;
        bool published = false;
        try
        {
            // Reuse the complete export/verification/cleanup path before touching an existing user file.
            var result = await ExportVoiceWithModelCoreAsync(reference.InputPath, completedPath, model, reference, progress, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                PocketTtsVoiceProfileFile.Publish(completedPath, destination, overwriteConfirmed, cancellationToken);
                published = true;
            }
            return result with { DestinationPath = destination };
        }
        catch (Exception exception) { operationFailure = exception; throw; }
        finally
        {
            // Publication consumes the staging file. Do no further cleanup I/O after committing a replacement.
            try { if (!published) { File.Delete(completedPath); } }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Voice export cleanup was incomplete. A staged profile may remain in the destination folder.",
                    operationFailure is null ? cleanup : new AggregateException(operationFailure, cleanup));
            }
        }
    }, cancellationToken);

    private Task<PocketTtsVoiceExportResult> ExportVoiceWithModelCoreAsync(string referenceAudioPath, string destinationPath,
        PocketTtsInstalledModel model, PreparedReferenceAudio reference, IProgress<PocketTtsVoiceExportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Task.Run(async () =>
        {
            EnsureSupportedProcessor();
            var audioRuntime = await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(cancellationToken).ConfigureAwait(false);
            if (reference.RuntimeIdentity is { } referenceIdentity && referenceIdentity != ReferenceRuntimeIdentity(audioRuntime.Identity))
            { throw new InvalidDataException("The active runtime changed. Reload reference audio before export."); }
            reference.RuntimeIdentity = ReferenceRuntimeIdentity(audioRuntime.Identity);
            // Capture one verified executable for this entire export; activation never replaces it.
            var cli = _executablePath is null
                ? new PocketTtsCliService(audioRuntime.ExecutablePath, _terminateProcessTree)
                : this;
            if (!string.Equals(cli.ResolveExecutablePath(), audioRuntime.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("The export runtime does not match the reference audio binding."); }
            using FileStream runtimeLease = File.OpenRead(cli.ResolveExecutablePath());
            await PocketTtsModelSetupService.CheckRuntimeIdentityAsync(cli.ResolveExecutablePath(), cancellationToken).ConfigureAwait(false);
            string[] supported = await PocketTtsModelSetupService.ValidateCliAsync(cli,
                !PocketTtsRuntimeStore.IsWithin(cli.ResolveExecutablePath(), PocketTtsRuntimeStore.RuntimesPath), cancellationToken).ConfigureAwait(false);
            if (!supported.Contains(model.Name, StringComparer.Ordinal))
            { throw new InvalidDataException("The model is not supported by the active runtime and cannot be used for voice export."); }
            using FileStream modelReadLease = await PocketTtsModelStore.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
            var registration = (await new PocketTtsModelInventoryService().ReadAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(entry => entry.Alias == model.Name);
            if (registration is null || model.InventoryIdentity is null ||
                PocketTtsModelInventoryService.GetIdentity(registration) != model.InventoryIdentity)
            { throw new InvalidDataException("The active model was removed or changed. Return to Settings and check models before exporting."); }
            IReadOnlyList<FileStream> assets = await model.OpenVerifiedAssetsAsync(cancellationToken).ConfigureAwait(false);
            string config = Path.Combine(Path.GetTempPath(), $"PocketTTSStudioLite-{Guid.NewGuid():N}.yaml");
            try
            {
                await File.WriteAllTextAsync(config, model.LocalConfiguration(), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(PocketTtsRuntimeStore.ResolveActivePath(), audioRuntime.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                { throw new InvalidDataException("The active runtime changed before export. Reprepare reference audio."); }
                // The CLI reserves this suffix for imported voice states. A recording that
                // Windows actually decoded under that misleading suffix must be sent as WAV.
                if (reference is { UsesOriginal: true } && Path.GetExtension(referenceAudioPath).Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
                {
                    referenceAudioPath = reference.EnsureFallbackWav(cancellationToken);
                    reference.RuntimeNeedsFallback = true;
                    progress?.Report(new("Using a Windows-decoded WAV because the input has the reserved voice-profile suffix."));
                }
                var result = await cli.ExportCoreAsync(referenceAudioPath, destinationPath, progress, cancellationToken, config).ConfigureAwait(false);
                if (reference is { UsesOriginal: true } && !result.Succeeded && result.Failure == PocketTtsVoiceExportFailure.Audio)
                {
                    // A native decoder may reject a variant of a normally supported format. The
                    // failed attempt publishes nothing; retry the raw decoded WAV, never conditioned audio.
                    if (!string.Equals(PocketTtsRuntimeStore.ResolveActivePath(), cli.ResolveExecutablePath(), StringComparison.OrdinalIgnoreCase))
                    { throw new InvalidDataException("Runtime changed before the audio fallback. Reprepare reference audio."); }
                    string fallback = reference.EnsureFallbackWav(cancellationToken);
                    reference.RuntimeNeedsFallback = true;
                    progress?.Report(new("The runtime could not decode the original file. Retrying with a Windows-decoded WAV..."));
                    result = await cli.ExportCoreAsync(fallback, destinationPath, progress, cancellationToken, config).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                foreach (FileStream asset in assets) { asset.Dispose(); }
                try { File.Delete(config); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { throw new InvalidOperationException("Export cleanup was incomplete. A temporary model configuration may remain.", exception); }
            }
        }, cancellationToken);
    }

    internal static string ReferenceRuntimeIdentity(PocketTtsModelSetupService.RuntimeIdentity identity) => identity.ReleaseVersion + "|" + identity.ExecutableSha256.ToUpperInvariant();

    private async Task<PocketTtsVoiceExportResult> ExportCoreAsync(
        string referenceAudioPath, string destinationPath,
        IProgress<PocketTtsVoiceExportProgress>? progress, CancellationToken cancellationToken, string configurationPath)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string audio = LocalPath(referenceAudioPath);
        string destination = LocalPath(destinationPath);
        if (Path.GetExtension(audio).Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Choose a local audio recording, not an existing voice profile.", nameof(referenceAudioPath)); }
        if (!File.Exists(audio)) { throw new FileNotFoundException("The reference audio file no longer exists. Select it again.", audio); }
        using (FileStream input = File.OpenRead(audio))
        {
            if (input.Length == 0) { throw new ArgumentException("The reference audio file is empty.", nameof(referenceAudioPath)); }
        }
        if (!Path.GetExtension(destination).Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("The export destination must end with .safetensors.", nameof(destinationPath)); }
        if (File.Exists(destination)) { throw new IOException("The destination already exists. Choose a new filename to preserve the existing file."); }
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) { throw new DirectoryNotFoundException("The destination folder no longer exists. Choose a destination again."); }
        // Native bytes are staged beside the destination, then renamed unchanged only after success.
        string temporaryPath = Path.Combine(directory, $".PocketTTSStudioLite-{Guid.NewGuid():N}.safetensors");
        using (new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        Exception? operationFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Exporting with the selected private model. This can take several minutes..."));
            var output = new PocketTtsExportOutput(progress);
            PocketTtsCliResult result = await ExecuteCoreAsync(
                ["export-voice", "--config", configurationPath, audio, temporaryPath], cancellationToken, output)
                .ConfigureAwait(false);
            if (!result.Succeeded) { return new(destination, result.ExitCode, output.Failure, output.ErrorMessage(result.ExitCode)); }
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            { return new(destination, result.ExitCode, PocketTtsVoiceExportFailure.Runtime, "The runtime reported success but produced no voice profile. Retry with a valid reference recording."); }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Saving the native voice profile…"));
            File.Move(temporaryPath, destination, overwrite: false);
            return new(destination, result.ExitCode, PocketTtsVoiceExportFailure.None, "Voice profile exported successfully.");
        }
        catch (Exception exception) { operationFailure = exception; throw; }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Voice export cleanup was incomplete. A temporary profile may remain in the destination folder.",
                    operationFailure is null ? cleanup : new AggregateException(operationFailure, cleanup));
            }
        }
    }

    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && (!uri.IsFile || uri.IsUnc))
        { throw new ArgumentException("Select a local file on this computer; URLs and network shares are not supported."); }
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) { throw new ArgumentException("Select a local file on this computer."); }
        return fullPath;
    }

    // File lookup and Process.Start can block, so run startup off the caller's UI thread too.
    private async Task<PocketTtsCliResult> ExecuteAsync(string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(DiagnosticTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await Task.Run(() => ExecuteCoreAsync(arguments, operation.Token), operation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation takes precedence if both signals are set during cleanup.
            throw new OperationCanceledException("Pocket TTS diagnostics were cancelled by the caller.",
                exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Pocket TTS diagnostics exceeded the 15-second timeout.", exception);
        }
    }

    private async Task<PocketTtsCliResult> ExecuteCoreAsync(
        string[] arguments, CancellationToken cancellationToken, PocketTtsExportOutput? exportOutput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupportedProcessor();
        using FileStream nativeReadLease = await PocketTtsModelStore.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
        var selected = _executablePath is null
            ? await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(cancellationToken, initializeIdentity: true).ConfigureAwait(false)
            : null;
        string executablePath = selected?.ExecutablePath ?? ResolveExecutablePath();
        using FileStream executableLease = File.OpenRead(executablePath);
        if (selected is not null && !string.Equals(
            Convert.ToHexString(await SHA256.HashDataAsync(executableLease, cancellationToken).ConfigureAwait(false)),
            selected.Identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("The active runtime changed before execution."); }
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (exportOutput is not null)
        {
            ConfigurePrivateModelEnvironment(startInfo);
        }

        await using var nativeOperation = new PocketTtsNativeOperation();
        PocketTtsNativeOperation.Running running;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            running = nativeOperation.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new FileNotFoundException(
                "The Pocket TTS executable was not found. Restore the application's runtime/pocket-tts files.",
                executablePath, exception);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start the Pocket TTS runtime at '{executablePath}'. " +
                "Check that it is a runnable Windows x64 executable and access is allowed.", exception);
        }
        using var runningLease = running;
        Process process = running.Process;
        // Drain both pipes concurrently. Reading one to completion first can deadlock the child.
        using StreamReader standardOutput = running.Output;
        using StreamReader standardError = running.Error;
        Task<string> stdout = ReadOutputAsync(standardOutput, exportOutput, cancellationToken);
        Task<string> stderr = ReadOutputAsync(standardError, exportOutput, cancellationToken);
        Task outputCompleted = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await nativeOperation.StopProcessesAsync().ConfigureAwait(false);
            await outputCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new PocketTtsCliResult(
                executablePath, process.ExitCode,
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            Exception? cleanupFailure = null;
            try
            {
                await StopOwnedProcessAsync(process).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                cleanupFailure = exception;
            }

            try
            {
                await outputCompleted.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                cleanupFailure ??= new InvalidOperationException(
                    "Pocket TTS stopped, but its output streams did not close during cleanup.", exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Observe cancelled reads even if process-tree cleanup failed.
            }
            catch (IOException)
            {
                // Preserve the original failure when termination closes a redirected pipe.
            }

            if (cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }

            throw;
        }
    }

    internal static void ConfigurePrivateModelEnvironment(ProcessStartInfo start)
    {
        // Explicit verified local config is mandatory; ambient cache locations cannot select assets.
        foreach (string variable in new[] { "HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "HF_ASSETS_CACHE", "HF_XET_CACHE",
            "XDG_CACHE_HOME", "TRANSFORMERS_CACHE", "PYTORCH_TRANSFORMERS_CACHE", "PYTORCH_PRETRAINED_BERT_CACHE", "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN" })
        { start.Environment.Remove(variable); }
        // The runtime still requires HOME; assets come from the verified private local config.
        if (!start.Environment.TryGetValue("HOME", out string? home) || string.IsNullOrWhiteSpace(home))
        { start.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1";
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, PocketTtsExportOutput? exportOutput, CancellationToken cancellationToken)
    {
        if (exportOutput is null) { return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
        char[] buffer = new char[4096];
        string tail = "";
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            string text = tail + new string(buffer, 0, count);
            exportOutput.Observe(text);
            tail = text[^Math.Min(512, text.Length)..];
        }
        // Export logs may contain credentials from the native HTTP stack: never return or persist them.
        return "";
    }

    private async Task StopOwnedProcessAsync(Process process)
    {
        AggregateException? partialTerminationFailure = null;
        try
        {
            if (!process.HasExited)
            {
                _terminateProcessTree(process);
            }
        }
        catch (AggregateException exception)
        {
            // A root exit does not mean its descendants were all terminated.
            partialTerminationFailure = exception;
            try
            {
                // Best effort to stop the owned root even when a descendant is inaccessible.
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception rootFailure) when (rootFailure is Win32Exception or InvalidOperationException)
            {
                partialTerminationFailure = new AggregateException(exception, rootFailure);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Natural exit can race cancellation.
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // The process exited between HasExited and Kill.
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Could not terminate the Pocket TTS child process after cancellation or failure.", exception);
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            if (partialTerminationFailure is not null)
            {
                throw IncompleteTreeTermination(new AggregateException(partialTerminationFailure, exception));
            }

            throw new InvalidOperationException(
                "The Pocket TTS child process did not exit during cleanup.", exception);
        }

        if (partialTerminationFailure is not null)
        {
            throw IncompleteTreeTermination(partialTerminationFailure);
        }
    }

    private static InvalidOperationException IncompleteTreeTermination(AggregateException exception) =>
        new("Pocket TTS process-tree termination was incomplete. One or more child processes may still be running; " +
            "cleanup could not be confirmed.", exception);

    internal static void EnsureSupportedProcessor()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            RuntimeInformation.OSArchitecture != Architecture.X64 || !Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "The verified Pocket TTS runtime requires Windows x64, an x64 application process, " +
                "and a processor with AVX2 and FMA enabled. This system cannot run that runtime safely.");
        }
    }

    private string ResolveExecutablePath()
    {
        string? configuredPath = _executablePath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return RequireExecutable(Path.GetFullPath(configuredPath, AppContext.BaseDirectory));
        }

        string appLocalPath = PocketTtsRuntimeStore.ResolveActivePath();
        if (File.Exists(appLocalPath))
        {
            return appLocalPath;
        }

        return RequireExecutable(appLocalPath);
    }

    private static string RequireExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The Pocket TTS executable was not found. Restore the validated runtime/pocket-tts application files and rebuild or reinstall the application.", path);
        }

        return path;
    }
}
