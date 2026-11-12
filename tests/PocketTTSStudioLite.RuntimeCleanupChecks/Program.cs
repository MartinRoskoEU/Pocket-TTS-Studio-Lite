using PocketTTSStudioLite.Services;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

string sandbox = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-RuntimeCleanupChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
int passed = 0;
bool createdBaseline = false;
byte[] baseline = Encoding.UTF8.GetBytes("isolated bundled runtime fixture");
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Fresh()
{
    PocketTtsModelSetupService.Root = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(PocketTtsModelSetupService.Root);
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
async Task<string> Seed(string tag, string contents)
{
    byte[] exe = Encoding.UTF8.GetBytes(contents), license = Encoding.UTF8.GetBytes("fixture license");
    string digest = Hash(exe), directory = PocketTtsRuntimeStore.ReleasePath(tag, digest);
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "pocket-tts.exe"), exe);
    File.WriteAllBytes(Path.Combine(directory, "LICENSE"), license);
    string name = $"pocket-tts-{tag[1..]}-x86_64-pc-windows-msvc.zip";
    await PocketTtsRuntimeStore.WriteJsonAsync(Path.Combine(directory, "runtime-installation.json"),
        new PocketTtsRuntimeStore.InstallationReceipt(tag, digest, new string('a', 64), 1, name,
            $"https://github.com/kyutai-labs/pocket-tts/releases/download/{tag}/{name}", Hash(license)), default);
    return directory;
}
async Task Activate(string directory)
{
    var verified = await PocketTtsRuntimeStore.VerifyRuntimeAsync(Path.Combine(directory, "pocket-tts.exe"), default, initializeIdentity: false);
    File.WriteAllText(PocketTtsModelSetupService.RuntimeIdentityPath, JsonSerializer.Serialize(verified.Identity));
}
async Task<(string Old, string Active)> Pair()
{
    Fresh();
    string old = await Seed("v4.2.0", "old executable");
    string active = await Seed("v4.3.0", "active executable");
    await Activate(active);
    return (old, active);
}
async Task<PocketTtsDownloadOperation> Retire(string directory)
{
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (string name in new[] { "pocket-tts.exe", "LICENSE", "runtime-installation.json" })
        hashes.Add("payload/" + name, await PocketTtsRuntimeStore.HashFileAsync(Path.Combine(directory, name), default));
    var retirement = await PocketTtsDownloadOperation.BeginRuntimeRetirementAsync(hashes, default);
    Directory.Move(directory, Path.Combine(retirement.DirectoryPath, "payload"));
    return retirement;
}
try
{
    // This is the test executable's output, never the application's installed baseline.
    Check(!File.Exists(PocketTtsRuntimeStore.BundledPath), "Preserve an existing test-output baseline.");
    Directory.CreateDirectory(Path.GetDirectoryName(PocketTtsRuntimeStore.BundledPath)!);
    using (var bundled = new FileStream(PocketTtsRuntimeStore.BundledPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) bundled.Write(baseline);
    createdBaseline = true;
    PocketTtsModelSetupService.ValidatedRuntimeSha256 = Hash(baseline);
    var pair = await Pair();
    using (var updateLock = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "runtime-update.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        var cleaned = await PocketTtsRuntimeStore.CleanupInactiveAsync(default);
        Check(cleaned.HadWork && !cleaned.CleanupPending && cleaned.DeletedBytes > 0 && !Directory.Exists(pair.Old) && Directory.Exists(pair.Active), "Inactive cleanup removed active runtime or retained old payload.");
        Check(!(await PocketTtsRuntimeStore.CleanupInactiveAsync(default)).HadWork, "Cleanup was not idempotent.");
    }
    passed++;

    pair = await Pair();
    File.WriteAllText(PocketTtsModelSetupService.RuntimeIdentityPath, JsonSerializer.Serialize(new PocketTtsModelSetupService.RuntimeIdentity(
        PocketTtsModelSetupService.ValidatedRuntimeRelease, PocketTtsModelSetupService.ValidatedRuntimeSha256)));
    var baselineCleanup = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(!baselineCleanup.CleanupPending && !Directory.Exists(pair.Old) && !Directory.Exists(pair.Active) &&
        File.ReadAllBytes(PocketTtsRuntimeStore.BundledPath).SequenceEqual(baseline), "Bundled active baseline was deleted or changed.");
    passed++;

    pair = await Pair();
    using (var busy = File.OpenRead(Path.Combine(pair.Old, "pocket-tts.exe")))
    {
        var cleaned = await PocketTtsRuntimeStore.CleanupInactiveAsync(default);
        Check(cleaned.CleanupPending && Directory.Exists(pair.Old) && Directory.Exists(pair.Active), "Busy runtime was moved/deleted.");
    }
    Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending && !Directory.Exists(pair.Old), "Startup did not retry busy runtime cleanup.");
    passed++;

    foreach (string failure in new[] { "changed exe", "changed license", "changed receipt", "unknown file", "unknown directory" })
    {
        pair = await Pair();
        string valid = await Seed("v4.1.0", "another old runtime");
        if (failure == "changed exe") File.AppendAllText(Path.Combine(pair.Old, "pocket-tts.exe"), "changed");
        if (failure == "changed license") File.AppendAllText(Path.Combine(pair.Old, "LICENSE"), "changed");
        if (failure == "changed receipt") File.WriteAllText(Path.Combine(pair.Old, "runtime-installation.json"), "{broken");
        if (failure == "unknown file") File.WriteAllText(Path.Combine(pair.Old, "user.txt"), "preserve");
        if (failure == "unknown directory") Directory.CreateDirectory(Path.Combine(pair.Old, "user"));
        var cleaned = await PocketTtsRuntimeStore.CleanupInactiveAsync(default);
        Check(cleaned.CleanupPending && Directory.Exists(pair.Old) && Directory.Exists(pair.Active) && !Directory.Exists(valid), "Invalid old runtime blocked independent cleanup or was removed: " + failure);
        passed++;
    }

    pair = await Pair();
    File.WriteAllText(PocketTtsModelSetupService.RuntimeIdentityPath, "{broken");
    var recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(recovered.CleanupPending && Directory.Exists(pair.Old) && Directory.Exists(pair.Active), "Invalid active identity authorized deletion.");
    passed++;

    pair = await Pair();
    using (var updateLock = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "runtime-update.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(recovered.CleanupPending && Directory.Exists(pair.Old), "Recovery bypassed runtime update lock.");
    }
    Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending && !Directory.Exists(pair.Old), "Recovery failed after installation lock release.");
    passed++;

    pair = await Pair();
    var retirement = await Retire(pair.Old);
    recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(!recovered.CleanupPending && !Directory.Exists(retirement.DirectoryPath) && !File.Exists(retirement.DirectoryPath + ".json") && Directory.Exists(pair.Active), "Retired payload was not recovered after interrupted move.");
    passed++;

    pair = await Pair(); retirement = await Retire(pair.Old);
    File.Delete(Path.Combine(retirement.DirectoryPath, "payload", "LICENSE"));
    Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending && !Directory.Exists(retirement.DirectoryPath), "Partially deleted retired runtime could not be resumed.");
    passed++;

    Fresh();
    string changedFile = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "retired-file-fixture");
    File.WriteAllText(changedFile, "changed bytes");
    try { PocketTtsModelStore.DeleteRegularFile(changedFile, localData: true, expectedSha256: new string('0', 64)); throw new Exception("Expected exclusive hash refusal."); }
    catch (InvalidDataException) { }
    Check(File.Exists(changedFile), "Deletion handle ignored expected retired-file hash.");
    passed++;

    pair = await Pair(); retirement = await Retire(pair.Old);
    File.AppendAllText(Path.Combine(retirement.DirectoryPath, "payload", "LICENSE"), "changed after move");
    recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(recovered.CleanupPending && recovered.DeletedBytes == 0 && File.Exists(Path.Combine(retirement.DirectoryPath, "payload", "pocket-tts.exe")) && File.Exists(retirement.DirectoryPath + ".json"), "Changed retired payload lost its files or journal.");
    passed++;

    pair = await Pair(); retirement = await Retire(pair.Old);
    File.WriteAllText(PocketTtsModelSetupService.RuntimeIdentityPath, "{broken");
    recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(recovered.CleanupPending && Directory.Exists(retirement.DirectoryPath), "Retirement recovery failed to reverify active runtime first.");
    passed++;

    pair = await Pair();
    string file = Path.Combine(pair.Old, "LICENSE"), original = file + ".original";
    File.Move(file, original);
    Check(Native.CreateHardLink(file, original, IntPtr.Zero), "Could not create runtime hardlink fixture.");
    // Remove the extra name from the owned directory, leaving its external hardlink in fixture storage.
    string outside = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); File.Move(original, outside);
    recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(recovered.CleanupPending && File.Exists(file) && File.Exists(outside), "Hardlinked runtime was accepted for deletion.");
    passed++;

    pair = await Pair();
    string displaced = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); Directory.Move(pair.Old, displaced);
    using (var junction = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "/c", "mklink", "/J", pair.Old, displaced } })!)
    { await junction.WaitForExitAsync(); Check(junction.ExitCode == 0, "Could not create runtime junction fixture."); }
    try
    {
        recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(recovered.CleanupPending && File.Exists(Path.Combine(displaced, "pocket-tts.exe")), "Runtime junction target was traversed/deleted.");
    }
    finally { Directory.Delete(pair.Old, recursive: false); }
    passed++;

    pair = await Pair();
    string unknown = Path.Combine(PocketTtsRuntimeStore.RuntimesPath, "user", new string('c', 64)); Directory.CreateDirectory(unknown);
    File.WriteAllText(Path.Combine(unknown, "user.txt"), "preserve");
    recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
    Check(!recovered.CleanupPending && Directory.Exists(unknown) && !Directory.Exists(pair.Old), "Unrecorded runtime directories were adopted or blocked valid cleanup.");
    passed++;

    pair = await Pair();
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await PocketTtsRuntimeStore.CleanupInactiveAsync(cancelled.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Check(Directory.Exists(pair.Old), "Cancellation deleted an inactive version before processing.");
    }
    passed++;

    // Production update install: mock network and CLI contract only, use a real x64
    // apphost as inert PE bytes in the ZIP. No downloaded executable is launched.
    foreach (bool busyOld in new[] { false, true })
    {
        pair = await Pair();
        using var handler = new ReleaseHandler(File.ReadAllBytes(Environment.ProcessPath!));
        using var http = new HttpClient(handler);
        var update = await new PocketTtsRuntimeUpdateService(http).CheckAsync();
        Check(update.Status == PocketTtsRuntimeUpdateStatus.UpdateAvailable, "Fixture release was not detected.");
        FileStream? busy = busyOld ? File.OpenRead(Path.Combine(pair.Active, "pocket-tts.exe")) : null;
        PocketTtsRuntimeInstallResult installed;
        try { installed = await new PocketTtsRuntimeInstallService(http, http).InstallAsync(update); }
        finally { busy?.Dispose(); }
        Check(installed.Succeeded && installed.CleanupIncomplete == busyOld && (await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(default)).Identity.ReleaseVersion == "v4.4.0", "Runtime activation/cleanup result incorrect.");
        Check(!Directory.Exists(pair.Old) && Directory.Exists(pair.Active) == busyOld, "Successful update did not retire the correct downloaded versions.");
        recovered = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(!recovered.CleanupPending && !Directory.Exists(pair.Active), "Startup failed to complete post-update retirement.");
        passed++;
    }

    pair = await Pair();
    using (var handler = new ReleaseHandler(File.ReadAllBytes(Environment.ProcessPath!)) { CorruptDownload = true })
    using (var http = new HttpClient(handler))
    {
        var update = await new PocketTtsRuntimeUpdateService(http).CheckAsync();
        var failed = await new PocketTtsRuntimeInstallService(http, http).InstallAsync(update);
        Check(!failed.Succeeded && !failed.CleanupIncomplete && Directory.Exists(pair.Old) && Directory.Exists(pair.Active) &&
            (await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(default)).Identity.ReleaseVersion == "v4.3.0", "Failed update retired files before successful activation.");
        Check(!Directory.EnumerateFileSystemEntries(PocketTtsRuntimeStore.StagingPath).Any(), "Failed ZIP verification left temporary cleanup incomplete.");
    }
    passed++;

    // Repair the selected release using its existing receipt, even when latest checks
    // cannot verify the missing executable. Exercise failures before any publication.
    Fresh();
    using (var handler = new ReleaseHandler(File.ReadAllBytes(Environment.ProcessPath!)))
    using (var http = new HttpClient(handler))
    {
        var previous = await Seed("v4.3.0", "previous executable");
        await Activate(previous);
        var service = new PocketTtsRuntimeInstallService(http, http);
        var update = await new PocketTtsRuntimeUpdateService(http).CheckAsync();
        Check((await service.InstallAsync(update)).Succeeded, "Repair fixture installation failed.");
        string executable = PocketTtsRuntimeStore.ResolveActivePath();
        string identity = File.ReadAllText(PocketTtsModelSetupService.RuntimeIdentityPath);
        string selection = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-selection.json");
        File.WriteAllText(selection, "saved model selection fixture");
        var target = PocketTtsRuntimeInstallService.ReadRepairTarget();
        string fingerprint = PocketTtsRuntimeInstallService.RepairIdentity(target);
        var existing = await service.RepairAsync(fingerprint);
        Check(!existing.Succeeded && File.Exists(executable), "Repair overwrote an existing runtime.");
        passed++;
        File.Delete(executable);
        var stale = await service.RepairAsync(fingerprint + "changed");
        Check(!stale.Succeeded && !File.Exists(executable), "Stale repair confirmation was accepted.");
        passed++;
        handler.CorruptDownload = true;
        var corrupt = await service.RepairAsync(fingerprint);
        Check(!corrupt.Succeeded && !File.Exists(executable), "Corrupt repair download was published.");
        passed++;
        handler.CorruptDownload = false;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = await service.RepairAsync(fingerprint, cancellationToken: cancellation.Token);
            Check(cancelled.Cancelled && !File.Exists(executable), "Repair cancellation published an executable.");
        }
        passed++;
        string license = Path.Combine(Path.GetDirectoryName(executable)!, "LICENSE");
        byte[] originalLicense = File.ReadAllBytes(license);
        File.WriteAllText(license, "changed license");
        var changedLicense = await service.RepairAsync(fingerprint);
        Check(!changedLicense.Succeeded && !File.Exists(executable), "Repair accepted a changed installed license.");
        File.WriteAllBytes(license, originalLicense);
        passed++;
        var repaired = await service.RepairAsync(fingerprint);
        Check(repaired.Succeeded && !repaired.CleanupIncomplete, "Missing downloaded runtime repair failed: " + repaired.Message);
        Check(File.ReadAllText(PocketTtsModelSetupService.RuntimeIdentityPath) == identity &&
            File.ReadAllText(selection) == "saved model selection fixture", "Repair changed saved identities or model selection.");
        Check((await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(default)).Identity == target.Identity,
            "Repaired executable does not match the saved identity.");
        passed++;
    }
    // A fresh installation may lose its bundled executable before saving identity.
    Fresh();
    byte[] baselineExecutable = File.ReadAllBytes(Environment.ProcessPath!);
    string bundledLicense = Path.Combine(Path.GetDirectoryName(PocketTtsRuntimeStore.BundledPath)!, "LICENSE");
    Check(!File.Exists(bundledLicense), "Preserve an existing fixture-output license.");
    try
    {
        PocketTtsModelSetupService.ValidatedRuntimeSha256 = Hash(baselineExecutable);
        File.Delete(PocketTtsRuntimeStore.BundledPath);
        File.WriteAllText(bundledLicense, "fixture license");
        using var handler = new ReleaseHandler(baselineExecutable, "v4.0.1");
        using var http = new HttpClient(handler);
        var target = PocketTtsRuntimeInstallService.ReadRepairTarget();
        var repaired = await new PocketTtsRuntimeInstallService(http, http).RepairAsync(PocketTtsRuntimeInstallService.RepairIdentity(target));
        Check(repaired.Succeeded && (await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(default)).Identity == target.Identity,
            "Bundled runtime repair without saved identity failed: " + repaired.Message);
        passed++;
    }
    finally
    {
        File.WriteAllBytes(PocketTtsRuntimeStore.BundledPath, baseline);
        PocketTtsModelSetupService.ValidatedRuntimeSha256 = Hash(baseline);
        File.Delete(bundledLicense);
    }

    foreach (var example in new[] { ("v0.2.2", true), ("v0.2.1", false), ("v0.2.0", false), ("v0.10.0", true) })
    {
        using var release = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = example.Item1, draft = false, prerelease = false }));
        var result = ApplicationUpdateService.Evaluate(release.RootElement, "0.2.1");
        Check(result.Available == example.Item2 && (result.ReleaseUrl is null ||
            result.ReleaseUrl.AbsoluteUri == "https://github.com/MartinRoskoEU/Pocket-TTS-Studio-Lite/releases/tag/" + example.Item1), "Incorrect application update result.");
        passed++;
    }
    foreach (string tag in new[] { "v0.3.0-rc.1", "../evil", "v01.2.3", "invalid" })
    {
        using var release = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false }));
        try { ApplicationUpdateService.Evaluate(release.RootElement, "0.2.1"); throw new Exception("Invalid application release accepted."); }
        catch (InvalidDataException) { }
        passed++;
    }
    foreach (bool draft in new[] { false, true })
    {
        using var release = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = "v0.3.0", draft, prerelease = !draft }));
        try { ApplicationUpdateService.Evaluate(release.RootElement, "0.2.1"); throw new Exception("Unpublished or prerelease application accepted."); }
        catch (InvalidDataException) { }
        passed++;
    }

    foreach (bool offline in new[] { false, true })
    {
        using var handler = new ApplicationFailureHandler(offline);
        using var http = new HttpClient(handler);
        var result = await new ApplicationUpdateService(http).CheckAsync(default);
        Check(!result.Available && result.ReleaseUrl is null && result.Message.Contains("Unable"),
            "Application check failure offered a release or escaped the optional checker.");
        passed++;
    }

    Console.WriteLine($"PASS: {passed} targeted runtime cleanup/install/repair and application update checks; fixture storage, mocked HTTP/CLI, no live runtime execution or user data.");
}
finally
{
    if (createdBaseline)
    {
        if (!File.ReadAllBytes(PocketTtsRuntimeStore.BundledPath).SequenceEqual(baseline)) throw new Exception("Changed fixture baseline was preserved.");
        File.Delete(PocketTtsRuntimeStore.BundledPath);
    }
    string resolved = Path.GetFullPath(sandbox);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("PocketTTSStudioLite-RuntimeCleanupChecks-", StringComparison.Ordinal))
        throw new Exception("Refusing unexpected runtime fixture cleanup path.");
    Directory.Delete(resolved, recursive: true);
}

internal static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLink(string link, string existing, IntPtr security);
}

internal sealed class ReleaseHandler : HttpMessageHandler
{
    private readonly string Tag, Name;
    private readonly byte[] _archive;
    internal bool CorruptDownload;
    internal ReleaseHandler(byte[] executable, string tag = "v4.4.0")
    {
        Tag = tag;
        Name = $"pocket-tts-{tag[1..]}-x86_64-pc-windows-msvc.zip";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("pocket-tts.exe").Open()) entry.Write(executable);
            using (var entry = zip.CreateEntry("LICENSE").Open()) entry.Write(Encoding.UTF8.GetBytes("fixture license"));
        }
        _archive = buffer.ToArray();
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.Host == "api.github.com")
        {
            var metadata = new { tag_name = Tag, draft = false, prerelease = false, assets = new[] { new { id = 42, name = Name, state = "uploaded", size = _archive.Length,
                digest = "sha256:" + Convert.ToHexString(SHA256.HashData(_archive)), browser_download_url = $"https://github.com/kyutai-labs/pocket-tts/releases/download/{Tag}/{Name}" } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json") });
        }
        byte[] download = _archive.ToArray();
        if (CorruptDownload) download[^1] ^= 1;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(download) });
    }
}

internal sealed class ApplicationFailureHandler(bool offline) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        offline ? throw new HttpRequestException("Offline fixture") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("malformed release metadata", Encoding.UTF8, "application/json") });
}
