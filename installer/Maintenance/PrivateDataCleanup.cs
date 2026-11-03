using PocketTTSStudioLite.Services;
using System.Text.Json;

namespace PocketTTSStudioLite.Maintenance;

internal sealed record DataCleanupPlan(int SchemaVersion, string AppId, string AppIdentity, string DataIdentity,
    OwnedFile[] Files, OwnedDirectory[] Directories);

internal sealed class PrivateDataCleanup(string appRoot)
{
    private const string PlanPath = "maintenance/uninstall-plan.json";
    private static string Root => PocketTtsRuntimeStore.LocalDataPath;
    private readonly List<OwnedDirectory> _temporaryDirectories = [];

    internal async Task RunAsync()
    {
        DataCleanupPlan? plan = File.Exists(MaintenanceFiles.Resolve(appRoot, PlanPath))
            ? await MaintenanceFiles.ReadAsync<DataCleanupPlan>(appRoot, PlanPath) : null;
        if (!Directory.Exists(Root))
        {
            if (plan is not null) Validate(plan, rootPresent: false);
            return;
        }
        PocketTtsModelStore.ValidatePath(Root, localData: true);
        var existingLocks = new[] { "model-install.lock", "runtime-update.lock", "models/.operations.lock" }.ToDictionary(name => name.Replace('/', Path.DirectorySeparatorChar),
            name => File.Exists(MaintenanceFiles.Resolve(Root, name)), StringComparer.OrdinalIgnoreCase);
        using var install = new FileStream(Path.Combine(Root, "model-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var runtime = new FileStream(Path.Combine(Root, "runtime-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var operations = PocketTtsModelStore.TryAcquireWrite();
        foreach (var locked in new[] { install, runtime, operations })
            if (locked.Length != 0) throw new InvalidDataException("Unexpected operation lock content was preserved.");
        var ownLocks = new[] { install, runtime, operations }.ToDictionary(stream => Path.GetRelativePath(Root, stream.Name), stream =>
            new OwnedFile(Path.GetRelativePath(Root, stream.Name), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])),
                PocketTtsModelStore.FileIdentity(stream.Name, stream.SafeFileHandle)), StringComparer.OrdinalIgnoreCase);
        await PocketTtsNativeOperation.EnsureQuiescentAsync(default);
        if (plan is null)
        {
            var files = await CaptureAsync();
            foreach (var locked in new[] { install, runtime, operations })
            {
                files.Add(ownLocks[Path.GetRelativePath(Root, locked.Name)]);
            }
            if (files.Count > 32768) throw new InvalidDataException("Private cleanup exceeds its supported plan limit. No files were deleted.");
            var directories = MaintenanceFiles.Directories(Root, files).Concat(_temporaryDirectories).DistinctBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(directory => directory.RelativePath.Length).ToArray();
            if (directories.Length > 32768) throw new InvalidDataException("Private cleanup exceeds its supported directory limit.");
            plan = new(1, MaintenanceFiles.AppId, PocketTtsModelStore.DirectoryIdentity(appRoot, appRoot),
                PocketTtsModelStore.DirectoryIdentity(Root), files.ToArray(), directories);
            await MaintenanceFiles.WriteAsync(appRoot, PlanPath, plan);
        }
        Validate(plan, rootPresent: true);
        // Validate every remaining planned file before deleting any in this attempt.
        foreach (var file in plan.Files)
        {
            if (ownLocks.TryGetValue(file.RelativePath, out var locked))
            {
                if (existingLocks[file.RelativePath] && locked.Identity != file.Identity) throw new InvalidDataException("An operation lock was replaced.");
                continue;
            }
            var actual = await MaintenanceFiles.CaptureAsync(Root, file.RelativePath, file.Sha256);
            if (actual is not null && actual.Identity != file.Identity) throw new InvalidDataException("A planned private file was replaced. It was preserved.");
        }
        foreach (var file in plan.Files.Where(file => !file.RelativePath.EndsWith(".lock", StringComparison.Ordinal))) Delete(file);
        operations.Dispose(); runtime.Dispose(); install.Dispose();
        foreach (var file in plan.Files.Where(file => file.RelativePath.EndsWith(".lock", StringComparison.Ordinal)))
            Delete(ownLocks.TryGetValue(file.RelativePath, out var current) && !existingLocks[file.RelativePath] ? current : file);
        MaintenanceFiles.Prune(Root, plan.Directories);
    }

    private void Validate(DataCleanupPlan plan, bool rootPresent)
    {
        if (plan.SchemaVersion != 1 || plan.AppId != MaintenanceFiles.AppId || plan.AppIdentity != PocketTtsModelStore.DirectoryIdentity(appRoot, appRoot) ||
            plan.Files is null || plan.Directories is null || plan.Files.Length > 32768 || plan.Directories.Length > 32768 ||
            plan.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Length ||
            (rootPresent && plan.DataIdentity != PocketTtsModelStore.DirectoryIdentity(Root))) throw new InvalidDataException("Private cleanup ownership changed.");
        foreach (var file in plan.Files) { MaintenanceFiles.Validate(file); _ = MaintenanceFiles.Resolve(Root, file.RelativePath); }
        foreach (var directory in plan.Directories) if (directory.RelativePath != ".") _ = MaintenanceFiles.Resolve(Root, directory.RelativePath);
    }

    private static void Delete(OwnedFile file) => PocketTtsModelStore.DeleteRegularFile(MaintenanceFiles.Resolve(Root, file.RelativePath),
        localData: true, expectedIdentity: file.Identity, expectedSha256: file.Sha256);

    private async Task<List<OwnedFile>> CaptureAsync()
    {
        var files = new Dictionary<string, OwnedFile>(StringComparer.OrdinalIgnoreCase);
        async Task Add(string relative, string? hash = null, bool gitBlob = false)
        {
            var file = await MaintenanceFiles.CaptureAsync(Root, relative, hash, gitBlob);
            if (file is not null) files[relative] = file;
        }
        var inventory = new PocketTtsModelInventoryService();
        foreach (var model in await inventory.ReadAsync())
        {
            foreach (string relative in PocketTtsModelInventoryService.BlobReferences(model))
            {
                string hash = Path.GetFileName(relative);
                await Add(Path.Combine("models", relative), hash, relative.Contains("git-blob-sha1", StringComparison.Ordinal));
            }
        }
        foreach (var job in await inventory.ReadPendingAsync())
            foreach (string relative in job.BlobPaths)
                await Add(Path.Combine("models", relative), Path.GetFileName(relative), relative.Contains("git-blob-sha1", StringComparison.Ordinal));
        string runtimes = PocketTtsRuntimeStore.RuntimesPath;
        PocketTtsModelStore.ValidatePath(runtimes, localData: true);
        if (Directory.Exists(runtimes))
            foreach (string release in Directory.EnumerateDirectories(runtimes))
            {
                if (Path.GetFileName(release) == ".staging") continue;
                PocketTtsModelStore.ValidatePath(release, localData: true);
                foreach (string directory in Directory.EnumerateDirectories(release))
                {
                    PocketTtsModelStore.ValidatePath(directory, localData: true);
                    if (!File.Exists(Path.Combine(directory, "runtime-installation.json"))) continue;
                    _ = await PocketTtsRuntimeStore.VerifyRuntimeAsync(Path.Combine(directory, "pocket-tts.exe"), default, initializeIdentity: false);
                    foreach (string name in new[] { "pocket-tts.exe", "LICENSE", "runtime-installation.json" })
                        await Add(Path.GetRelativePath(Root, Path.Combine(directory, name)));
                }
            }
        var snapshot = await PocketTtsDownloadOperation.CaptureForUninstallAsync();
        foreach (var directory in snapshot.Directories) _temporaryDirectories.Add(new(Path.GetRelativePath(Root, directory.Key), directory.Value));
        foreach (var temporary in snapshot.Files)
            await Add(Path.GetRelativePath(Root, temporary.Key), temporary.Value);
        foreach (string name in new[] { "model-inventory.json", "model-inventory.previous.json", "runtime-identity.json", "runtime-identity.previous.json",
            "model-selection.json", "voice-export.json", "model-update-checks.json", "model-update-checks.previous.json" })
        {
            string path = MaintenanceFiles.Resolve(Root, name);
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            PocketTtsModelStore.ValidateFile(path, stream.SafeFileHandle, localData: true);
            if (stream.Length > 16 * 1024 * 1024) continue;
            try
            {
                using var json = await JsonDocument.ParseAsync(stream);
                if (ValidMetadata(name, json.RootElement)) await Add(name);
            }
            catch (JsonException) { /* Invalid optional metadata remains unknown content. */ }
        }
        foreach (string relative in new[] { "model-install.lock", "runtime-update.lock", "model-inventory.lock", "model-update-checks.lock", "models/.operations.lock" })
        {
            string path = MaintenanceFiles.Resolve(Root, relative);
            if (!File.Exists(path)) continue;
            // Our three operation locks are already exclusively held; inspect their handles below.
            if (relative is "model-install.lock" or "runtime-update.lock" or "models/.operations.lock") continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0) await Add(relative);
        }
        return files.Values.ToList();
    }

    private static bool ValidMetadata(string name, JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) return false;
        string[] keys = document.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) return false;
        bool Shape(params string[] allowed) => keys.All(key => allowed.Contains(key, StringComparer.Ordinal));
        if (name.StartsWith("runtime-identity", StringComparison.Ordinal))
        {
            if (keys.Length != 2 || !Shape("ReleaseVersion", "ExecutableSha256") || !document.TryGetProperty("ReleaseVersion", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !document.TryGetProperty("ExecutableSha256", out var digest) || digest.ValueKind != JsonValueKind.String || !PocketTtsRuntimeStore.IsHash(digest.GetString())) return false;
            try { PocketTtsRuntimeUpdateService.ValidateReleaseTag(tag.GetString()!); return true; } catch (FormatException) { return false; }
        }
        if (!document.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version)) return false;
        if (name.StartsWith("model-inventory", StringComparison.Ordinal))
        {
            if (version != 2 || !Shape("SchemaVersion", "Models", "PendingCleanups") || !document.TryGetProperty("Models", out var models) || models.ValueKind != JsonValueKind.Array) return false;
            try
            {
                var records = models.Deserialize<PocketTtsModelInventoryEntry[]>();
                if (records is null || records.Select(model => model.Alias).Distinct(StringComparer.Ordinal).Count() != records.Length) return false;
                foreach (var model in records) _ = PocketTtsModelInventoryService.BlobReferences(model);
                return true;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or NullReferenceException) { return false; }
        }
        if (version != 1) return false;
        if (name == "model-selection.json") return keys.Length == 2 && Shape("SchemaVersion", "SelectedModelAlias") &&
            document.TryGetProperty("SelectedModelAlias", out var alias) && alias.ValueKind is JsonValueKind.String or JsonValueKind.Null;
        if (name == "voice-export.json") return keys.Length == 2 && Shape("SchemaVersion", "ExportFolder") &&
            document.TryGetProperty("ExportFolder", out var folder) && folder.ValueKind == JsonValueKind.String;
        return keys.Length == 3 && Shape("SchemaVersion", "Models", "LastSuccessfulCheckUtc") && document.TryGetProperty("Models", out var results) && results.ValueKind == JsonValueKind.Array &&
            document.TryGetProperty("LastSuccessfulCheckUtc", out var stamp) && stamp.ValueKind is JsonValueKind.Null or JsonValueKind.String;
    }
}
