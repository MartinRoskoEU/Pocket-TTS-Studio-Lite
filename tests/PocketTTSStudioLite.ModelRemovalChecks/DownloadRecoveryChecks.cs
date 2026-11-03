using PocketTTSStudioLite.Services;
using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class DownloadRecoveryChecks
{
    internal static async Task<int> RunAsync(string sandbox)
    {
        int passed = 0;
        const string Name = "0123456789abcdef0123456789abcdef.partial";
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Download recovery: " + message);
        }
        void Fresh()
        {
            PocketTtsRuntimeStore.LocalDataPath = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PocketTtsRuntimeStore.LocalDataPath);
        }
        string Journal(PocketTtsDownloadOperation operation) => operation.DirectoryPath + ".json";
        void Change(PocketTtsDownloadOperation operation, Action<JsonObject> change)
        {
            var record = JsonNode.Parse(File.ReadAllText(Journal(operation)))!.AsObject();
            change(record);
            File.WriteAllText(Journal(operation), record.ToJsonString());
        }
        async Task<PocketTtsDownloadOperation> Model()
        {
            var operation = await PocketTtsDownloadOperation.BeginModelAsync([Name], default);
            Check(File.Exists(Journal(operation)) && !Directory.EnumerateFileSystemEntries(operation.DirectoryPath).Any(),
                "Ownership must be durable before any payload is created.");
            return operation;
        }

        Fresh();
        var operation = await Model();
        string file = Path.Combine(operation.DirectoryPath, Name);
        File.WriteAllText(file, "partial model");
        var result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.HadWork && !result.CleanupPending && result.DeletedBytes == 13 && !Directory.Exists(operation.DirectoryPath) && !File.Exists(Journal(operation)),
            "Interrupted model download was not fully cleaned up.");
        Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).HadWork, "Completed cleanup was not idempotent.");
        passed++;

        Fresh(); operation = await Model();
        File.Move(Journal(operation), operation.DirectoryPath + ".tmp");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(!result.CleanupPending && !Directory.Exists(operation.DirectoryPath) && !File.Exists(operation.DirectoryPath + ".tmp"),
            "Complete interrupted journal publication was not recoverable.");
        passed++;

        Fresh(); operation = await Model();
        file = Path.Combine(operation.DirectoryPath, Name);
        File.WriteAllText(file, "known");
        string unknown = Path.Combine(operation.DirectoryPath, "user.txt");
        File.WriteAllText(unknown, "private content");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.CleanupPending && result.DeletedBytes == 0 && File.Exists(file) && File.Exists(unknown) && File.Exists(Journal(operation)),
            "Unknown operation content did not stop preflight before deletion.");
        passed++;

        Fresh(); operation = await Model();
        file = Path.Combine(operation.DirectoryPath, Name); File.WriteAllText(file, "busy");
        using (var busy = File.OpenRead(file))
        {
            result = await PocketTtsDownloadOperation.RecoverAsync(default);
            Check(result.CleanupPending && result.DeletedBytes == 0 && File.Exists(Journal(operation)), "Busy temporary file lost retry authority.");
        }
        Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending && !File.Exists(file), "Busy-file retry failed.");
        passed++;

        foreach (string kind in new[] { "model", "runtime" })
        {
            Fresh(); operation = kind == "model" ? await Model() : await PocketTtsDownloadOperation.BeginRuntimeAsync(default);
            file = Path.Combine(operation.DirectoryPath, kind == "model" ? Name : "runtime.zip");
            File.WriteAllText(file, "active download");
            using (var active = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath,
                kind == "model" ? "model-install.lock" : "runtime-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                result = await PocketTtsDownloadOperation.RecoverAsync(default);
                Check(result.CleanupPending && result.DeletedBytes == 0 && File.Exists(file), "Recovery raced an active " + kind + " operation.");
            }
            Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending, "Installation lock release did not allow retry.");
            passed++;
        }

        Fresh(); operation = await PocketTtsDownloadOperation.BeginRuntimeAsync(default);
        Directory.CreateDirectory(Path.Combine(operation.DirectoryPath, "payload"));
        foreach (string name in new[] { "runtime.zip", "payload/pocket-tts.exe", "payload/LICENSE", "payload/runtime-installation.json" })
            File.WriteAllText(Path.Combine(operation.DirectoryPath, name), "partial");
        string installed = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "runtimes", "keep-installed");
        Directory.CreateDirectory(installed); File.WriteAllText(Path.Combine(installed, "pocket-tts.exe"), "installed");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(!result.CleanupPending && result.DeletedBytes == 28 && !Directory.Exists(operation.DirectoryPath) && File.Exists(Path.Combine(installed, "pocket-tts.exe")),
            "Exact runtime temporary layout was not cleaned or installed runtime was touched.");
        passed++;

        Fresh(); operation = await Model();
        Directory.Delete(operation.DirectoryPath); // Crash after final directory deletion, before journal acknowledgement.
        Check(!(await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending && !File.Exists(Journal(operation)), "Missing operation directory was not retryable.");
        passed++;

        Fresh(); operation = await Model();
        string moved = operation.DirectoryPath + "-original";
        Directory.Move(operation.DirectoryPath, moved);
        Directory.CreateDirectory(operation.DirectoryPath);
        File.WriteAllText(Path.Combine(operation.DirectoryPath, Name), "replacement");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.CleanupPending && File.Exists(Path.Combine(operation.DirectoryPath, Name)) && Directory.Exists(moved), "Replaced physical operation directory was adopted.");
        passed++;

        Fresh(); operation = await Model();
        string originalDirectory = operation.DirectoryPath;
        string originalRoot = PocketTtsRuntimeStore.LocalDataPath;
        Fresh();
        Directory.CreateDirectory(PocketTtsModelStore.Staging);
        string copied = Path.Combine(PocketTtsModelStore.Staging, Path.GetFileName(originalDirectory));
        Directory.CreateDirectory(copied); File.WriteAllText(Path.Combine(copied, Name), "unowned copy");
        File.Copy(originalDirectory + ".json", copied + ".json");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.CleanupPending && File.Exists(Path.Combine(copied, Name)) && Directory.Exists(originalRoot), "Record copied to a different physical store granted ownership.");
        passed++;

        foreach (Action<JsonObject> corrupt in new Action<JsonObject>[]
        {
            record => record["Files"] = new JsonArray("../outside.partial"),
            record => record["Files"] = new JsonArray(Name, Name),
            record => record["SchemaVersion"] = 99,
            record => record["Kind"] = "runtime",
            record => record["StoreIdentity"] = new string('0', 40),
            record => record["Extra"] = "unknown field",
            record => record["Files"] = null,
        })
        {
            Fresh(); operation = await Model();
            file = Path.Combine(operation.DirectoryPath, Name); File.WriteAllText(file, "preserve");
            Change(operation, corrupt);
            result = await PocketTtsDownloadOperation.RecoverAsync(default);
            Check(result.CleanupPending && result.DeletedBytes == 0 && File.Exists(file) && File.Exists(Journal(operation)), "Invalid record granted deletion authority.");
            passed++;
        }

        Fresh(); operation = await Model();
        File.WriteAllText(Journal(operation), "{broken");
        var valid = await Model(); File.WriteAllText(Path.Combine(valid.DirectoryPath, Name), "valid");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.CleanupPending && File.Exists(Journal(operation)) && !File.Exists(Journal(valid)), "One corrupt record blocked unrelated valid cleanup.");
        passed++;

        Fresh(); operation = await Model();
        File.Copy(Journal(operation), operation.DirectoryPath + ".tmp");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.CleanupPending && File.Exists(Journal(operation)) && File.Exists(operation.DirectoryPath + ".tmp"), "Conflicting journals were adopted.");
        passed++;

        Fresh(); operation = await Model();
        string external = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); File.WriteAllText(external, "external");
        Check(Native.CreateHardLink(Path.Combine(operation.DirectoryPath, Name), external, IntPtr.Zero), "Could not create hardlink fixture.");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(result.CleanupPending && File.Exists(external) && File.Exists(Journal(operation)), "Hardlink was accepted as a temporary independent file.");
        passed++;

        Fresh(); operation = await Model();
        Directory.CreateDirectory(Path.Combine(operation.DirectoryPath, "unknown-subtree"));
        Check((await PocketTtsDownloadOperation.RecoverAsync(default)).CleanupPending && File.Exists(Journal(operation)), "Unknown directory was traversed.");
        passed++;

        Fresh();
        string orphan = Path.Combine(PocketTtsModelStore.Staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan); File.WriteAllText(Path.Combine(orphan, Name), "legacy");
        result = await PocketTtsDownloadOperation.RecoverAsync(default);
        Check(!result.HadWork && File.Exists(Path.Combine(orphan, Name)), "Unrecorded legacy staging was adopted.");
        passed++;

        Fresh(); operation = await Model();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await PocketTtsDownloadOperation.RecoverAsync(cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Check(File.Exists(Journal(operation)), "Cancellation lost the cleanup journal.");
        }
        passed++;

        Fresh(); operation = await Model();
        file = Path.Combine(operation.DirectoryPath, Name); File.WriteAllText(file, "original");
        string expectedIdentity;
        using (var original = File.OpenRead(file)) expectedIdentity = PocketTtsModelStore.FileIdentity(file, original.SafeFileHandle);
        File.Move(file, file + ".original"); File.WriteAllText(file, "replacement");
        try { PocketTtsModelStore.DeleteRegularFile(file, localData: true, expectedIdentity: expectedIdentity); throw new Exception("Expected identity refusal."); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(file) == "replacement", "Changed planned file was deleted.");
        passed++;

        Fresh(); operation = await Model();
        file = Path.Combine(operation.DirectoryPath, Name); File.WriteAllText(file, "preserve");
        Change(operation, record => record["Extra"] = "changed after publication");
        try { await operation.CleanupAsync(); throw new Exception("Expected journal refusal."); }
        catch (InvalidDataException) { }
        Check(File.Exists(file) && File.Exists(Journal(operation)), "Immediate cleanup accepted changed metadata.");
        passed++;

        foreach (bool payloadLink in new[] { false, true })
        {
            Fresh(); operation = payloadLink ? await PocketTtsDownloadOperation.BeginRuntimeAsync(default) : await Model();
            string target = Path.Combine(sandbox, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
            string targetFile = Path.Combine(target, payloadLink ? "pocket-tts.exe" : Name); File.WriteAllText(targetFile, "outside");
            string link = payloadLink ? Path.Combine(operation.DirectoryPath, "payload") : operation.DirectoryPath;
            if (!payloadLink) Directory.Delete(link);
            using (var junction = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/c", "mklink", "/J", link, target },
            })!)
            { await junction.WaitForExitAsync(); Check(junction.ExitCode == 0, "Could not create temporary junction fixture."); }
            try
            {
                result = await PocketTtsDownloadOperation.RecoverAsync(default);
                Check(result.CleanupPending && result.DeletedBytes == 0 && File.ReadAllText(targetFile) == "outside" && File.Exists(Journal(operation)),
                    "Operation/payload junction authorized external deletion.");
            }
            finally { Directory.Delete(link, recursive: false); }
            passed++;
        }

        Console.WriteLine($"PASS: {passed} targeted temporary-download ownership/recovery checks.");
        return passed;
    }
}
