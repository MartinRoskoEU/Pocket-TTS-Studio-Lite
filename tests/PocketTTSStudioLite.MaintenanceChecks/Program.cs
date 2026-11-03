using PocketTTSStudioLite.Maintenance;
using PocketTTSStudioLite.Services;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

if (args.Length != 1) throw new Exception("Pass the freshly built InstallerSupport.dll path.");
string sandbox = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-MaintenanceChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
string app = "";
InstallationMaintenance maintenance = null!;
PackageManifest package = null!;
void Fresh()
{
    string test = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
    app = Path.Combine(test, "application"); PocketTtsModelSetupService.Root = Path.Combine(test, "data");
    maintenance = new(app);
    package = new(1, MaintenanceFiles.AppId, "0.2.0", [new("app.exe", Hash("new app"))]);
}
async Task Install()
{
    await maintenance.PrepareAsync(Hash("package")); File.WriteAllText(Path.Combine(app, "app.exe"), "new app");
    Check(await maintenance.CommitAsync(package, Hash("package")), "Fresh install commit failed.");
}
void SeedModel()
{
    string digest = Hash("weights").ToLowerInvariant(); string blob = PocketTtsModelStore.BlobPath(true, digest);
    Directory.CreateDirectory(Path.GetDirectoryName(blob)!); File.WriteAllText(blob, "weights");
    var model = new PocketTtsModelInventoryEntry("english", "kyutai/pocket-tts", new string('a', 40), PocketTtsModelStore.Root,
        [new(Path.GetRelativePath(PocketTtsModelStore.Root, blob), "weights", 7, digest, "SHA256", true)], "fixture");
    File.WriteAllText(PocketTtsModelInventoryService.InventoryPath, JsonSerializer.Serialize(new { SchemaVersion = 2, Models = new[] { model, model with { Alias = "french" } } }));
}
IntPtr module = NativeLibrary.Load(Path.GetFullPath(args[0]));
var begin = Marshal.GetDelegateForFunctionPointer<Begin>(NativeLibrary.GetExport(module, "BeginOwnedDelete"));
var queue = Marshal.GetDelegateForFunctionPointer<Queue>(NativeLibrary.GetExport(module, "QueueOwnedFile"));
var commit = Marshal.GetDelegateForFunctionPointer<Commit>(NativeLibrary.GetExport(module, "CommitOwnedDelete"));
var cancel = Marshal.GetDelegateForFunctionPointer<Cancel>(NativeLibrary.GetExport(module, "CancelOwnedDelete"));
try
{
    Fresh(); await Install();
    File.WriteAllText(Path.Combine(app, "unknown.txt"), "user");
    await maintenance.PrepareAsync(Hash("package2"));
    File.WriteAllText(Path.Combine(app, "app.exe"), "new version");
    var replacement = package with { Files = [new("app.exe", Hash("new version"))] };
    Check(await maintenance.CommitAsync(replacement, Hash("package2")) && File.Exists(Path.Combine(app, "unknown.txt")), "Reinstall removed unknown content.");
    passed++;

    Fresh();
    Directory.CreateDirectory(app); File.WriteAllText(Path.Combine(app, "legacy.dll"), "legacy");
    await maintenance.PrepareAsync(Hash("package"));
    Check(maintenance.LegacyFilesPreserved, "Uninventoried 0.1.0 files were adopted.");
    File.WriteAllText(Path.Combine(app, "app.exe"), "new app");
    await maintenance.CommitAsync(package, Hash("package"));
    Check(File.Exists(Path.Combine(app, "legacy.dll")), "Legacy file was swept."); passed++;

    Fresh(); await Install();
    await maintenance.PrepareAsync(Hash("next"));
    replacement = package with { Files = [new("replacement.exe", Hash("replacement"))] };
    File.WriteAllText(Path.Combine(app, "replacement.exe"), "replacement");
    using (var busy = File.OpenRead(Path.Combine(app, "app.exe")))
        Check(!await maintenance.CommitAsync(replacement, Hash("next")), "Busy obsolete payload falsely completed cleanup.");
    var receipt = await MaintenanceFiles.ReadAsync<InstallationReceipt>(app, InstallationMaintenance.ReceiptPath);
    Check(receipt.PendingObsolete.Length == 1 && await maintenance.CleanupObsoleteAsync(receipt) && !File.Exists(Path.Combine(app, "app.exe")), "Pending obsolete cleanup could not resume."); passed++;

    Fresh(); await Install();
    File.AppendAllText(Path.Combine(app, "app.exe"), "changed");
    try { await maintenance.PrepareAsync(Hash("next")); throw new Exception("Expected changed payload refusal."); } catch (InvalidDataException) { }
    Check(File.Exists(Path.Combine(app, "app.exe")), "Changed payload was deleted."); passed++;

    Fresh(); await Install(); SeedModel();
    File.WriteAllText(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "unknown.txt"), "user");
    await maintenance.UninstallAsync(Hash("package"), false);
    Check(File.Exists(PocketTtsModelInventoryService.InventoryPath) && File.Exists(Path.Combine(app, "app.exe")), "Retain-data mode removed data or application before native phase."); passed++;

    Fresh(); await Install(); SeedModel();
    string blobPath = Directory.GetFiles(Path.Combine(PocketTtsModelStore.Root, "blobs", "sha256")).Single();
    File.WriteAllText(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "unknown.txt"), "user");
    File.WriteAllText(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-selection.json"), "{\"SchemaVersion\":1,\"UnknownUserContent\":true}");
    await maintenance.UninstallAsync(Hash("package"), true);
    Check(!File.Exists(blobPath) && !File.Exists(PocketTtsModelInventoryService.InventoryPath) && File.Exists(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "unknown.txt")) &&
        File.Exists(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-selection.json")), "Verified-data cleanup deleted unknown files or retained model blobs."); passed++;

    Fresh(); await Install(); SeedModel();
    blobPath = Directory.GetFiles(Path.Combine(PocketTtsModelStore.Root, "blobs", "sha256")).Single();
    using (var busy = File.OpenRead(blobPath))
    {
        try { await maintenance.UninstallAsync(Hash("package"), true); throw new Exception("Expected busy-data refusal."); } catch (IOException) { }
        Check(File.Exists(Path.Combine(app, "maintenance", "uninstall-plan.json")) && File.Exists(blobPath), "Busy cleanup lost its resumable plan.");
    }
    await maintenance.UninstallAsync(Hash("package"), true);
    Check(!File.Exists(blobPath) && File.Exists(Path.Combine(app, "maintenance", "uninstall-state.ini")), "Data cleanup could not resume."); passed++;

    Fresh(); await Install(); SeedModel();
    blobPath = Directory.GetFiles(Path.Combine(PocketTtsModelStore.Root, "blobs", "sha256")).Single();
    File.AppendAllText(blobPath, "changed");
    try { await maintenance.UninstallAsync(Hash("package"), true); throw new Exception("Expected corrupt blob refusal."); } catch (InvalidDataException) { }
    Check(File.Exists(PocketTtsModelInventoryService.InventoryPath) && File.Exists(blobPath), "Corrupt data was deleted before preflight completed."); passed++;

    Fresh(); Directory.CreateDirectory(app);
    File.WriteAllText(Path.Combine(app, "owned.dll"), "owned"); File.WriteAllText(Path.Combine(app, "unknown.txt"), "user");
    Check(begin(app) && queue("owned.dll", Hash("owned")) && commit(), "Native owned payload deletion failed.");
    Check(!File.Exists(Path.Combine(app, "owned.dll")) && File.Exists(Path.Combine(app, "unknown.txt")), "Native deletion swept unknown files."); passed++;

    Fresh(); Directory.CreateDirectory(app); File.WriteAllText(Path.Combine(app, "first"), "first"); File.WriteAllText(Path.Combine(app, "changed"), "changed");
    Check(begin(app) && queue("first", Hash("first")) && !queue("changed", Hash("expected")), "Native preflight accepted changed bytes."); cancel();
    Check(File.Exists(Path.Combine(app, "first")), "Native queue deleted files before whole preflight."); passed++;

    Check(begin(app) && !queue("..\\outside", Hash("outside")), "Native relative-path boundary failed."); cancel(); passed++;
    Check(begin(app) && queue("missing", Hash("missing")) && queue("first", Hash("first")) && commit(), "Native retry did not accept already removed planned files."); passed++;

    Fresh(); await Install(); SeedModel();
    var temporary = await PocketTtsDownloadOperation.BeginModelAsync([new string('a', 32) + ".partial"], default);
    var emptyOperation = await PocketTtsDownloadOperation.BeginModelAsync([new string('b', 32) + ".partial"], default);
    File.WriteAllText(Path.Combine(temporary.DirectoryPath, new string('a', 32) + ".partial"), "partial");
    await maintenance.UninstallAsync(Hash("package"), true);
    Check(!File.Exists(temporary.DirectoryPath + ".json") && !Directory.Exists(temporary.DirectoryPath) && !Directory.Exists(emptyOperation.DirectoryPath), "Uninstall failed to remove recorded staging or empty owned operation directories."); passed++;

    Fresh(); await Install(); await maintenance.PrepareAsync(Hash("resume"));
    replacement = package with { Files = [new("app.exe", Hash("partial upgrade")), new("extra.dll", Hash("extra"))] };
    File.WriteAllText(Path.Combine(app, "app.exe"), "partial upgrade");
    try { await maintenance.CommitAsync(replacement, Hash("resume")); throw new Exception("Expected incomplete install."); } catch (InvalidDataException) { }
    await maintenance.PrepareAsync(Hash("resume"));
    File.WriteAllText(Path.Combine(app, "extra.dll"), "extra");
    Check(await maintenance.CommitAsync(replacement, Hash("resume")), "Interrupted publication could not be resumed with the same package."); passed++;

    Fresh(); await Install(); SeedModel();
    string external = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "profile.safetensors"), "profile");
    File.WriteAllText(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "voice-export.json"), JsonSerializer.Serialize(new { SchemaVersion = 1, ExportFolder = external }));
    await maintenance.UninstallAsync(Hash("package"), true);
    Check(File.Exists(Path.Combine(external, "profile.safetensors")) && !File.Exists(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "voice-export.json")), "Export preference cleanup followed its external folder."); passed++;

    Fresh(); await Install(); SeedModel();
    blobPath = Directory.GetFiles(Path.Combine(PocketTtsModelStore.Root, "blobs", "sha256")).Single();
    using (var busy = File.OpenRead(blobPath))
    { try { await maintenance.UninstallAsync(Hash("package"), true); } catch (IOException) { } }
    string oldRoot = PocketTtsRuntimeStore.LocalDataPath;
    Directory.Move(oldRoot, oldRoot + "-original"); Directory.CreateDirectory(oldRoot); File.WriteAllText(Path.Combine(oldRoot, "unknown.txt"), "replacement root");
    try { await maintenance.UninstallAsync(Hash("package"), true); throw new Exception("Expected replaced-root refusal."); } catch (InvalidDataException) { }
    Check(File.Exists(Path.Combine(oldRoot, "unknown.txt")), "Cleanup plan adopted a replaced data root."); passed++;

    Fresh(); Directory.CreateDirectory(app);
    string externalFile = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); File.WriteAllText(externalFile, "external");
    Check(Native.CreateHardLink(Path.Combine(app, "hardlink"), externalFile, IntPtr.Zero), "Could not create native hardlink fixture.");
    Check(begin(app) && !queue("hardlink", Hash("external")), "Native payload guard accepted a hardlink."); cancel();
    Check(File.ReadAllText(externalFile) == "external", "Native guard changed an external hardlink target."); passed++;

    Fresh(); Directory.CreateDirectory(app);
    external = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "file"), "outside");
    string link = Path.Combine(app, "linked");
    using (var junction = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "/c", "mklink", "/J", link, external } })!)
    { await junction.WaitForExitAsync(); Check(junction.ExitCode == 0, "Could not create maintenance junction fixture."); }
    try { Check(begin(app) && !queue("linked\\file", Hash("outside")), "Native payload guard traversed a junction."); cancel(); }
    finally { Directory.Delete(link, recursive: false); }
    Check(File.Exists(Path.Combine(external, "file")), "Native payload guard deleted junction target content."); passed++;

    Fresh(); Directory.CreateDirectory(app);
    externalFile = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); File.WriteAllText(externalFile, "existing");
    Check(Native.CreateHardLink(Path.Combine(app, "app.exe"), externalFile, IntPtr.Zero), "Could not create setup target hardlink fixture.");
    try { await maintenance.PrepareAsync(Hash("package"), package.Files); throw new Exception("Expected linked installation target refusal."); } catch (InvalidDataException) { }
    Check(File.ReadAllText(externalFile) == "existing", "Setup preflight modified a linked target."); passed++;

    Fresh(); await maintenance.PrepareAsync(Hash("package"));
    Check(begin(Path.Combine(app, "maintenance")), "Cannot pin maintenance fixture directory.");
    try
    {
        using var instance = new FileStream(Path.Combine(app, "maintenance", "operation.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        File.WriteAllText(Path.Combine(app, "app.exe"), "new app");
        Check(await maintenance.CommitAsync(package, Hash("package")), "Pinned directory/instance guard blocked receipt publication.");
    }
    finally { cancel(); }
    passed++;

    Fresh(); Directory.CreateDirectory(app);
    string loadedBootstrap = Path.Combine(app, "InstallerSupport.dll");
    File.Copy(Path.GetFullPath(args[0]), loadedBootstrap);
    IntPtr mappedBootstrap = NativeLibrary.Load(loadedBootstrap);
    try
    {
        bool overwriteBlocked = false;
        try { File.Copy(Path.GetFullPath(args[0]), loadedBootstrap, overwrite: true); }
        catch (IOException) { overwriteBlocked = true; }
        catch (UnauthorizedAccessException) { overwriteBlocked = true; }
        Check(overwriteBlocked, "Fixture failed to reproduce the loaded-bootstrap overwrite conflict.");
        // Preparation must reuse the existing mapped input, hash it and copy it
        // to the separate maintenance path, never extract/write over that input.
        string copy = Path.Combine(app, "PocketMaintenance.dll");
        File.Copy(loadedBootstrap, copy);
        using var source = File.OpenRead(loadedBootstrap);
        using var destination = File.OpenRead(copy);
        Check(SHA256.HashData(source).SequenceEqual(SHA256.HashData(destination)), "Loaded bootstrap could not be read/copied intact.");
    }
    finally { NativeLibrary.Free(mappedBootstrap); }
    passed++;

    Fresh(); await Install();
    await maintenance.UninstallAsync(Hash("package"), false);
    var uninstallFiles = (await MaintenanceFiles.ReadAsync<InstallationReceipt>(app, InstallationMaintenance.ReceiptPath)).Files;
    var uninstallReceipt = (await MaintenanceFiles.CaptureAsync(app, InstallationMaintenance.ReceiptPath))!;
    var uninstallState = (await MaintenanceFiles.CaptureAsync(app, "maintenance/uninstall-state.ini"))!;
    string instancePath = Path.Combine(app, "maintenance", "operation.lock");
    using (var instance = new FileStream(instancePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
    {
        Check(begin(app), "Cannot begin final uninstall fixture.");
        foreach (var file in uninstallFiles.Append(uninstallReceipt).Append(uninstallState))
            Check(queue(file.RelativePath, file.Sha256), "Cannot queue final uninstall file: " + file.RelativePath);
        Check(commit() && !File.Exists(Path.Combine(app, "maintenance", "uninstall-state.ini")), "Successful native removal retained uninstall recovery state.");
        Check(Directory.Exists(Path.Combine(app, "maintenance")), "Fixture did not retain the active operation lock directory.");
    }
    // Inno's registered dirifempty pass runs only after the operation lock/pins
    // are released. No recursive deletion or unknown-content adoption is needed.
    Directory.Delete(Path.Combine(app, "maintenance"), recursive: false);
    Directory.Delete(app, recursive: false);
    Check(!Directory.Exists(app), "Completed uninstall retained its application directories."); passed++;

    Fresh(); await Install();
    await maintenance.UninstallAsync(Hash("package"), false);
    uninstallState = (await MaintenanceFiles.CaptureAsync(app, "maintenance/uninstall-state.ini"))!;
    Check(begin(app) && queue("maintenance/uninstall-state.ini", uninstallState.Sha256) && !queue("app.exe", Hash("changed")), "Uninstall failure fixture did not fail preflight.");
    cancel();
    Check(File.Exists(Path.Combine(app, "maintenance", "uninstall-state.ini")) && File.Exists(Path.Combine(app, "app.exe")), "Failed preflight removed recovery state or application files."); passed++;

    Console.WriteLine($"PASS: {passed} targeted installer maintenance/data/native deletion checks; isolated fixtures only, no registry, installed product or installer execution.");
}
finally
{
    cancel(); NativeLibrary.Free(module);
    string resolved = Path.GetFullPath(sandbox);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("PocketTTSStudioLite-MaintenanceChecks-", StringComparison.Ordinal)) throw new Exception("Unexpected fixture cleanup root.");
    Directory.Delete(resolved, recursive: true);
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool Begin([MarshalAs(UnmanagedType.LPWStr)] string root);
[UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool Queue([MarshalAs(UnmanagedType.LPWStr)] string relative, [MarshalAs(UnmanagedType.LPWStr)] string hash);
[UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool Commit();
[UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Cancel();
internal static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
