using System.ComponentModel;
using System.Diagnostics;
using PocketTTSStudioLite.Services;

// This executable also acts as a controlled subprocess for pipe and cancellation checks.
if (args is ["fixture-idle"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args is ["--version"] && Environment.GetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION") != "1")
{
    await Task.WhenAll(
        Console.Out.WriteAsync(new string('o', 192 * 1024)),
        Console.Error.WriteAsync(new string('e', 192 * 1024)));
    Environment.ExitCode = 23;
    return;
}

if (args is ["export-voice", "--help"] or ["--version"])
{
    using Process child = StartSelf("fixture-idle");
    string pidFile = Environment.GetEnvironmentVariable("POCKET_TTS_TEST_PID_FILE")!;
    await File.WriteAllTextAsync(pidFile + ".tmp", $"{Environment.ProcessId} {child.Id}");
    File.Move(pidFile + ".tmp", pidFile);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args is ["check-unsupported"])
{
    Check(!System.Runtime.Intrinsics.X86.Avx2.IsSupported && !System.Runtime.Intrinsics.X86.Fma.IsSupported,
        "The verification subprocess must have AVX2 and FMA disabled.");
    try
    {
        await new PocketTtsCliService("missing.exe").GetVersionAsync();
        throw new Exception("An unsupported processor was accepted.");
    }
    catch (PlatformNotSupportedException exception)
    {
        Check(exception.Message.Contains("AVX2") && exception.Message.Contains("FMA"), "CPU error must explain requirements.");
    }

    return;
}

string repositoryRoot = FindRepositoryRoot();
string runtimeExe = Path.Combine(repositoryRoot,
    "development", "runtime", "pocket-tts", "v4.0.1", "windows-x64", "extracted",
    "pocket-tts-3.3.0-x86_64-pc-windows-msvc", "pocket-tts.exe");
string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Pocket TTS CLI checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
string? originalOverride = Environment.GetEnvironmentVariable("POCKET_TTS_EXECUTABLE");
string originalWorkingDirectory = Environment.CurrentDirectory;
string? originalPidFile = Environment.GetEnvironmentVariable("POCKET_TTS_TEST_PID_FILE");
string? originalHangVersion = Environment.GetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION");
bool hardeningOnly = args is ["--hardening"];
int passed = 0;
try
{
    Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", null);
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", null);
    Environment.CurrentDirectory = temporaryDirectory;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

#if DEBUG
    await Verify("Explicit runtime path independent of working directory", async () =>
    {
        PocketTtsCliResult result = await new PocketTtsCliService(runtimeExe).GetVersionAsync(deadline.Token);
        Check(result.Succeeded && result.StandardOutput.Trim() == "pocket-tts 3.3.0", "Unexpected runtime version.");
        Check(result.ExecutablePath == runtimeExe && result.StandardError == "", "Wrong runtime or unexpected stderr.");
    }, hardening: true);
#endif

    await Verify("App-local runtime resolution", async () =>
    {
        string appLocalDirectory = Path.Combine(AppContext.BaseDirectory, "runtime", "pocket-tts");
        Check(!Directory.Exists(appLocalDirectory), "Preserve an existing app-local runtime rather than overwrite it.");
        Directory.CreateDirectory(appLocalDirectory);
        string appLocalExe = Path.Combine(appLocalDirectory, "pocket-tts.exe");
        try
        {
            File.Copy(runtimeExe, appLocalExe);
            PocketTtsCliResult result = await new PocketTtsCliService().GetVersionAsync(deadline.Token);
            Check(result.Succeeded && result.ExecutablePath == appLocalExe, "App-local runtime was not selected.");
        }
        finally
        {
            File.Delete(appLocalExe);
            Directory.Delete(appLocalDirectory);
        }
    });

    await Verify("Real export help with a relative executable path containing spaces", async () =>
    {
        string relativePath = Path.GetRelativePath(AppContext.BaseDirectory, runtimeExe);
        PocketTtsCliResult result = await new PocketTtsCliService(relativePath).GetExportVoiceHelpAsync(deadline.Token);
        Check(result.ExitCode == 0 && result.StandardError == "", "Export help failed.");
        Check(result.StandardOutput.Contains("<AUDIO_PATH>") && result.StandardOutput.Contains("--config"), "Missing CLI help.");
    }, hardening: true);

    await Verify("Explicit relative override resolves relative to the application", async () =>
    {
        Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", Path.GetRelativePath(AppContext.BaseDirectory, runtimeExe));
        try
        {
            PocketTtsCliResult result = await new PocketTtsCliService(Path.GetRelativePath(AppContext.BaseDirectory, runtimeExe)).GetVersionAsync(deadline.Token);
            Check(result.Succeeded && result.ExecutablePath == runtimeExe, "Override was not used.");
        }
        finally { Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", null); }
    });

    await Verify("Missing executable produces a clear error without fallback", async () =>
    {
        string missingPath = Path.Combine(temporaryDirectory, "missing.exe");
        Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", missingPath);
        try
        {
            var exception = await Expect<FileNotFoundException>(() => new PocketTtsCliService(missingPath).GetVersionAsync());
            Check(exception.FileName == missingPath && exception.Message.Contains("runtime/pocket-tts"), "Unclear missing-file error.");
            // An explicit path has precedence over even a valid environment override.
            Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", runtimeExe);
            await Expect<FileNotFoundException>(() => new PocketTtsCliService(missingPath).GetVersionAsync());
        }
        finally { Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", null); }
    });

    await Verify("Invalid executable reports a launch failure", async () =>
    {
        string invalidExe = Path.Combine(temporaryDirectory, "invalid.exe");
        await File.WriteAllTextAsync(invalidExe, "This is not an executable.");
        var exception = await Expect<InvalidOperationException>(() => new PocketTtsCliService(invalidExe).GetVersionAsync());
        Check(exception.InnerException is Win32Exception, "Native launch error was lost.");
    });

    await Verify("Pre-cancelled request does not resolve or launch a process", async () =>
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Expect<OperationCanceledException>(() => new PocketTtsCliService("missing.exe").GetVersionAsync(cancelled.Token));
    }, hardening: true);

    await Verify("Concurrent stdout/stderr drain and nonzero exit capture", async () =>
    {
        PocketTtsCliResult result = await new PocketTtsCliService(Environment.ProcessPath!).GetVersionAsync(deadline.Token);
        Check(result.ExitCode == 23 && !result.Succeeded, "Nonzero exit code was lost.");
        Check(result.StandardOutput == new string('o', 192 * 1024), "stdout was truncated.");
        Check(result.StandardError == new string('e', 192 * 1024), "stderr was truncated.");
    }, hardening: true);

    await Verify("Cancellation stops the owned tree and preserves an unrelated process",
        () => VerifyInterruptedTree(timeout: false, partialFailure: false), hardening: true);
    await Verify("Version diagnostics time out after 15 seconds and stop the owned tree",
        () => VerifyInterruptedTree(timeout: true, partialFailure: false, version: true), hardening: true);
    await Verify("Export-help diagnostics time out after 15 seconds and stop the owned tree",
        () => VerifyInterruptedTree(timeout: true, partialFailure: false), hardening: true);
    await Verify("Partial termination overrides cancellation and best-effort stops the root",
        () => VerifyInterruptedTree(timeout: false, partialFailure: true), hardening: true);
    await Verify("Partial termination overrides timeout even when the root already exited",
        () => VerifyInterruptedTree(timeout: true, partialFailure: true), hardening: true);

    await Verify("Unsupported AVX2/FMA is rejected before executable lookup", async () =>
    {
        var info = SelfStartInfo("check-unsupported");
        // .NET 10 groups FMA under this AVX2 switch; verify both report unsupported.
        info.Environment["DOTNET_EnableAVX2"] = "0";
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.ExitCode == 0, "CPU guard check failed: " + await stderr);
            await stdout;
        }
        finally { await StopFixture(process); }
    });

    Console.WriteLine($"{passed} targeted runtime checks passed.");
}
finally
{
    Environment.CurrentDirectory = originalWorkingDirectory;
    Environment.SetEnvironmentVariable("POCKET_TTS_EXECUTABLE", originalOverride);
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_PID_FILE", originalPidFile);
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", originalHangVersion);
    Directory.Delete(temporaryDirectory, recursive: true);
}

async Task Verify(string name, Func<Task> check, bool hardening = false)
{
    if (hardeningOnly && !hardening) { return; }
    await check();
    passed++;
    Console.WriteLine("PASS " + name);
}

async Task VerifyInterruptedTree(bool timeout, bool partialFailure, bool version = false)
{
    string pidFile = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".pids");
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_PID_FILE", pidFile);
    Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", version ? "1" : null);
    using Process unrelated = StartSelf("fixture-idle");
    using var cancellation = new CancellationTokenSource();
    var partialError = new AggregateException("Simulated descendant failures",
        new Win32Exception(5, "First descendant could not be terminated."),
        new Win32Exception(5, "Second descendant could not be terminated."));
    var service = partialFailure
        ? new PocketTtsCliService(Environment.ProcessPath!, process =>
        {
            if (timeout)
            {
                process.Kill();
                Check(process.WaitForExit(5000), "Fixture root did not exit.");
            }
            throw partialError;
        })
        : new PocketTtsCliService(Environment.ProcessPath!);
    var elapsed = Stopwatch.StartNew();
    Task<PocketTtsCliResult> operation = version
        ? service.GetVersionAsync(cancellation.Token)
        : service.GetExportVoiceHelpAsync(cancellation.Token);
    Process? parent = null;
    Process? child = null;
    try
    {
        await WaitForFile(pidFile);
        string[] ids = (await File.ReadAllTextAsync(pidFile)).Split(' ');
        parent = Process.GetProcessById(int.Parse(ids[0]));
        child = Process.GetProcessById(int.Parse(ids[1]));
        Check(!operation.IsCompleted, "Diagnostic operation blocked or exited before interruption.");
        if (!timeout) { cancellation.Cancel(); }
        Func<Task> wait = async () => await operation.WaitAsync(TimeSpan.FromSeconds(30));
        if (partialFailure)
        {
            var exception = await Expect<InvalidOperationException>(wait);
            Check(exception.Message.Contains("incomplete") && exception.Message.Contains("may still be running"),
                "Partial cleanup must not be reported as successful cancellation or timeout.");
            Check(ReferenceEquals(exception.InnerException, partialError), "Aggregate termination details were lost.");
            Check(!child.HasExited, "The partial-failure fixture must leave its descendant alive for verification.");
        }
        else if (timeout)
        {
            var exception = await Expect<TimeoutException>(wait);
            Check(exception.Message.Contains("15-second"), "Timeout must identify the diagnostic deadline.");
            Check(exception.InnerException is OperationCanceledException, "Timeout cause was lost.");
        }
        else
        {
            var exception = await Expect<OperationCanceledException>(wait);
            Check(exception.CancellationToken == cancellation.Token, "Original caller token was lost.");
        }

        if (timeout)
        {
            Check(elapsed.Elapsed >= TimeSpan.FromSeconds(14), "Diagnostic deadline expired too early.");
            Check(!cancellation.IsCancellationRequested, "Default timeout must not cancel the caller's token source.");
        }

        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (!partialFailure) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        Check(parent.HasExited && !unrelated.HasExited, "Interruption affected the wrong process.");
    }
    finally
    {
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
        if (parent is not null) { await StopFixture(parent); parent.Dispose(); }
        if (child is not null) { await StopFixture(child); child.Dispose(); }
        await StopFixture(unrelated);
        Environment.SetEnvironmentVariable("POCKET_TTS_TEST_HANG_VERSION", null);
    }
}

static void Check(bool condition, string message)
{
    if (!condition) { throw new Exception(message); }
}

static async Task<T> Expect<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T exception) { return exception; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static ProcessStartInfo SelfStartInfo(string argument)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add(argument);
    return info;
}

static Process StartSelf(string argument) => Process.Start(SelfStartInfo(argument))!;

static async Task StopFixture(Process process)
{
    if (!process.HasExited) { process.Kill(entireProcessTree: true); }
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}

static async Task WaitForFile(string path)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(path)) { await Task.Delay(10, timeout.Token); }
}

static string FindRepositoryRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PocketTTSStudioLite", "PocketTTSStudioLite.csproj")))
        {
            return directory.FullName;
        }
    }

    throw new DirectoryNotFoundException("Run the checks from a build inside this checkout.");
}
