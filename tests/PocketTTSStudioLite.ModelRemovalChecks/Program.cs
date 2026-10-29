using PocketTTSStudioLite.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

if (args.FirstOrDefault() == "--native-fixture")
{
    Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
    Console.Error.WriteLine("fixture stderr");
    return;
}
if (args.FirstOrDefault() == "--linger") { await Task.Delay(TimeSpan.FromMinutes(2)); return; }
if (args.FirstOrDefault() == "--spawn-child")
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--linger" }, UseShellExecute = false, CreateNoWindow = true })!;
    Console.WriteLine(child.Id);
    return;
}

string sandbox = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-RemovalChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
int passed = 0, skipped = 0;
void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
void Fresh()
{
    PocketTtsRuntimeStore.LocalDataPath = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(PocketTtsModelStore.Root);
}
PocketTtsModelInventoryEntry Model(string alias, params string[] contents)
{
    var files = contents.Select((content, index) =>
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string path = PocketTtsModelStore.BlobPath(true, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) { File.WriteAllBytes(path, bytes); }
        return new PocketTtsModelInventoryFile(Path.GetRelativePath(PocketTtsModelStore.Root, path), "asset" + index, bytes.Length, hash, "SHA256", index == 0);
    }).ToArray();
    string yaml = string.Join('\n', files.Select((file, index) => (index == 0 ? "weights_path" : "tokenizer_path") + ": hf://kyutai/pocket-tts/" + file.RepositoryPath + "@" + new string('a', 40))) + "\n";
    return new(alias, "kyutai/pocket-tts", new string('a', 40), PocketTtsModelStore.Root, files, yaml);
}
void Seed(params PocketTtsModelInventoryEntry[] models) => File.WriteAllText(PocketTtsModelInventoryService.InventoryPath, JsonSerializer.Serialize(new { SchemaVersion = 2, Models = models }));
string Blob(PocketTtsModelInventoryEntry model, int index = 0) => Path.Combine(PocketTtsModelStore.Root, model.RequiredFiles[index].RelativePath);
var inventory = new PocketTtsModelInventoryService();
var remover = new PocketTtsModelRemovalService();
async Task Commit(PocketTtsModelInventoryEntry model)
{
    using var install = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var operation = PocketTtsModelStore.TryAcquireWrite();
    await inventory.CommitRemovalAsync(model.Alias, PocketTtsModelInventoryService.GetIdentity(model), default);
}
try
{
    Fresh();
    var first = Model("first", "first weights", "shared tokenizer");
    var second = Model("second", "second weights", "shared tokenizer") with { ConfigurationYaml = "invalid config: deliberately unverified" };
    Seed(first, second);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    File.WriteAllText(PocketTtsModelUpdateCoordinator.SettingsPath, JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, LastSuccessfulCheckUtc = now,
        Models = new[] { first, second }.Select(model => new { Result = new PocketTtsModelUpdateResult(model.Alias, PocketTtsModelUpdateStatus.UpToDate, "fixture", new string('b', 40)) { InstalledIdentity = PocketTtsModelInventoryService.GetIdentity(model) }, LastSuccessfulCheckUtc = now })
    }));
    var removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
    Check(removed.Removed && !removed.CleanupPending && removed.DeletedBytes == first.RequiredFiles[0].SizeBytes && removed.SharedFilesRetained == 1, "Shared-file removal result incorrect.");
    Check(!File.Exists(Blob(first)) && File.Exists(Blob(first, 1)) && File.Exists(Blob(second)), "Shared/unverified remaining model assets lost.");
    using (var settings = JsonDocument.Parse(File.ReadAllText(PocketTtsModelUpdateCoordinator.SettingsPath)))
    { Check(settings.RootElement.GetProperty("Models").GetArrayLength() == 1 && settings.RootElement.GetProperty("Models")[0].GetProperty("Result").GetProperty("Alias").GetString() == "second", "Update cache invalidation lost another model or retained removed model."); }
    Check((await inventory.ReadPendingAsync()).Count == 0 && (await inventory.ReadAsync()).Single().Alias == "second", "Registration/journal incorrect.");
    passed++;
    removed = await remover.RemoveAsync(second.Alias, PocketTtsModelInventoryService.GetIdentity(second));
    Check(removed.Removed && !removed.CleanupPending && !File.Exists(Blob(second)) && !File.Exists(Blob(second, 1)), "Last owner did not remove shared blob/unverified model.");
    passed++;

    Fresh(); first = Model("busy", "busy weights"); Seed(first);
    using (var read = await PocketTtsModelStore.AcquireReadAsync(default))
    {
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(!removed.Removed && (await inventory.ReadAsync()).Count == 1 && File.Exists(Blob(first)), "Active operation did not block removal before commit.");
    }
    passed++;
    using (var install = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    { Check(!(await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first))).Removed, "Installation did not block removal."); }
    passed++;
    using (var writer = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-inventory.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(!removed.Removed && removed.DeletedBytes == 0 && File.Exists(Blob(first)) && (await inventory.ReadPendingAsync()).Count == 0, "Failed inventory commit deleted files or created a journal.");
    }
    passed++;
    using (var busy = File.OpenRead(Blob(first)))
    {
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(removed.Removed && removed.CleanupPending && removed.DeletedBytes == 0 && (await inventory.ReadPendingAsync()).Count == 1, "Busy blob did not preserve durable cleanup.");
    }
    var recovery = await remover.RecoverAsync(default);
    Check(recovery is { CleanupPending: false } && !File.Exists(Blob(first)) && (await inventory.ReadPendingAsync()).Count == 0, "Interrupted cleanup recovery failed.");
    passed++;

    Fresh(); first = Model("update-busy", "update busy weights"); Seed(first);
    using (var updateLock = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-update-checks.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(removed.Removed && removed.CleanupPending && !File.Exists(Blob(first)) && (await inventory.ReadPendingAsync()).Count == 1, "Failed cache invalidation lost its recovery obligation.");
    }
    recovery = await remover.RecoverAsync(default);
    Check(recovery is { CleanupPending: false, DeletedBytes: 0 } && (await inventory.ReadPendingAsync()).Count == 0, "Cache invalidation recovery failed or double-counted deletion."); passed++;

    Fresh(); first = Model("crash", "reused weights"); Seed(first); await Commit(first);
    Check(File.Exists(Blob(first)) && (await inventory.ReadAsync()).Count == 0 && (await inventory.ReadPendingAsync()).Count == 1, "Commit deleted before cleanup or lost journal.");
    second = first with { Alias = "reinstalled" };
    await inventory.RegisterAfterInstallationAsync(second, replaceExisting: false);
    recovery = await remover.RecoverAsync(default);
    Check(recovery is { CleanupPending: false, DeletedBytes: 0, SharedFilesRetained: 1 } && File.Exists(Blob(second)) && (await inventory.ReadAsync()).Count == 1, "Recovery deleted a newly referenced asset or registration lost pending journal.");
    passed++;

    Fresh(); first = Model("cancel", "cancel weights"); Seed(first);
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { _ = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first), cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { }
        Check((await inventory.ReadAsync()).Count == 1 && File.Exists(Blob(first)), "Cancellation changed registration/files.");
    }
    Check(!(await remover.RemoveAsync(first.Alias, new string('f', 64))).Removed && File.Exists(Blob(first)), "Stale confirmation removed model.");
    passed++;
    second = first with { Alias = "unsafe", RequiredFiles = [first.RequiredFiles[0] with { RelativePath = "..\\outside" }] };
    Seed(first, second);
    Check(!(await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first))).Removed && File.Exists(Blob(first)), "Unsafe remaining reference permitted deletion.");
    passed++;

    Fresh(); first = Model("hardlink", "hardlinked bytes"); Seed(first);
    string extra = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "outside-models-hardlink");
    Check(Native.CreateHardLink(extra, Blob(first), IntPtr.Zero), "Could not create hardlink fixture.");
    removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
    Check(removed.Removed && removed.CleanupPending && removed.DeletedBytes == 0 && File.Exists(extra) && File.Exists(Blob(first)), "Hardlink was deleted.");
    File.Delete(extra); await remover.RecoverAsync(default); passed++;

    Fresh(); first = Model("junction", "junction bytes"); Seed(first);
    string blobDirectory = Path.GetDirectoryName(Blob(first))!;
    string outsideDirectory = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "outside-models-directory");
    Directory.Move(blobDirectory, outsideDirectory);
    using (var junction = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        ArgumentList = { "/c", "mklink", "/J", blobDirectory, outsideDirectory },
    })!)
    { await junction.WaitForExitAsync(); Check(junction.ExitCode == 0, "Could not create directory-junction fixture."); }
    try
    {
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(removed.Removed && removed.CleanupPending && removed.DeletedBytes == 0 && File.Exists(Path.Combine(outsideDirectory, Path.GetFileName(Blob(first)))), "Junction was traversed or external file deleted.");
    }
    finally { Directory.Delete(blobDirectory, recursive: false); }
    passed++;

    Fresh(); first = Model("symlink", "symlink bytes"); Seed(first);
    extra = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "outside-models-original");
    File.Move(Blob(first), extra);
    try
    {
        File.CreateSymbolicLink(Blob(first), extra);
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(removed.Removed && removed.CleanupPending && removed.DeletedBytes == 0 && File.Exists(extra), "Symlink was followed or deleted.");
        File.Delete(Blob(first)); passed++;
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException && (exception.HResult & 0xFFFF) == 1314)
    { skipped++; Console.WriteLine("SKIP: symlink creation needs Windows Developer Mode or privilege."); }

    Fresh(); first = Model("guard", "guarded bytes"); Seed(first);
    string guards = Path.Combine(PocketTtsModelStore.Root, ".native-operations"); Directory.CreateDirectory(guards);
    string id = Guid.NewGuid().ToString("N"), guard = Path.Combine(guards, id + ".json");
    File.WriteAllText(guard, JsonSerializer.Serialize(new { JobName = "Local\\PocketTTSStudioLite-" + id, Assigned = false }));
    removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
    Check(!removed.Removed && File.Exists(Blob(first)), "Unresolved native marker did not block removal."); passed++;
    File.WriteAllText(guard, JsonSerializer.Serialize(new { JobName = "Local\\PocketTTSStudioLite-" + id, Assigned = true }));
    removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
    Check(removed.Removed && !removed.CleanupPending && !File.Exists(guard), "Terminated named-job recovery failed."); passed++;

    Fresh();
    string[] tricky = ["", "with spaces", "quotes\"inside", "trailing\\", "double\\\"quote", "žluťoučký", "line\nbreak"];
    await using (var operation = new PocketTtsNativeOperation())
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = sandbox };
        start.ArgumentList.Add("--native-fixture"); foreach (string argument in tricky) { start.ArgumentList.Add(argument); }
        using var running = operation.Start(start);
        var stdout = running.Output.ReadToEndAsync(); var stderr = running.Error.ReadToEndAsync();
        await running.Process.WaitForExitAsync(); await operation.StopProcessesAsync();
        Check(JsonSerializer.Deserialize<string[]>(await stdout)!.SequenceEqual(tricky) && (await stderr).Contains("fixture stderr"), "Contained native arguments/redirected output changed.");
    }
    Check(!Directory.EnumerateFiles(Path.Combine(PocketTtsModelStore.Root, ".native-operations")).Any(), "Native marker was not cleared after confirmed exit."); passed++;
    first = Model("native-active", "active native weights"); Seed(first);
    await using (var operation = new PocketTtsNativeOperation())
    {
        using var running = operation.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--linger" }, UseShellExecute = false, WorkingDirectory = sandbox });
        removed = await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first));
        Check(!removed.Removed && File.Exists(Blob(first)), "Active native session allowed removal.");
        await operation.StopProcessesAsync();
    }
    Check((await remover.RemoveAsync(first.Alias, PocketTtsModelInventoryService.GetIdentity(first))).Removed, "Confirmed native termination did not unblock removal."); passed++;
    await using (var operation = new PocketTtsNativeOperation())
    {
        using var running = operation.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--spawn-child" }, UseShellExecute = false, WorkingDirectory = sandbox });
        var stdout = running.Output.ReadToEndAsync(); var stderr = running.Error.ReadToEndAsync();
        await running.Process.WaitForExitAsync(); await operation.StopProcessesAsync();
        int childId = int.Parse((await stdout).Trim()); await stderr;
        try { using var child = Process.GetProcessById(childId); Check(child.HasExited, "Contained child survived native cleanup."); }
        catch (ArgumentException) { }
    }
    passed++;
    Console.WriteLine($"PASS: {passed} targeted removal/native-lifecycle checks; {skipped} skipped. Only temporary fixture data and fixture native processes were used.");
}
finally
{
    string resolved = Path.GetFullPath(sandbox);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("PocketTTSStudioLite-RemovalChecks-", StringComparison.Ordinal))
    { throw new Exception("Refusing unexpected fixture cleanup path."); }
    Directory.Delete(resolved, recursive: true);
}

internal static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
