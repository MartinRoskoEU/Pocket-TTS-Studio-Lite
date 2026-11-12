using PocketTTSStudioLite.Services;
using System.Text.Json;

namespace PocketTTSStudioLite.Maintenance;

internal sealed partial class InstallationMaintenance
{
    private sealed record PreparedPayloadFile(string RelativePath, string CandidateSha256, OwnedFile? Original, OwnedFile? Backup);

    private async Task BackupPayloadAsync(string packageHash, PackageFile[] files, Action checkCancellation)
    {
        var candidates = files.Append(new PackageFile("maintenance/package.json", packageHash)).ToArray();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidates)
            for (string? parent = Path.GetDirectoryName(MaintenanceFiles.Resolve(Root, file.Path));
                parent is not null && (parent == Root || PocketTtsRuntimeStore.IsWithin(parent, Root)); parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent)) directories.Add(Path.GetRelativePath(Root, parent));
        if (_preparation!.RootCreated) directories.Remove(".");
        if (_preparation.MaintenanceCreated) directories.Remove("maintenance");
        _preparation = _preparation with { Payload = [], ExistingDirectories = directories.ToArray(), BackupFiles = [] };
        if (_preparation.PreviousReceipt is not null)
            _preparation = _preparation with { PreviousInstallation = await MaintenanceFiles.ReadAsync<InstallationReceipt>(Root, ReceiptPath) };

        foreach (var file in candidates)
        {
            checkCancellation();
            var original = await MaintenanceFiles.CaptureAsync(Root, file.Path);
            OwnedFile? backup = null;
            if (original is not null)
            {
                if (_preparation.RollbackDirectory is null)
                {
                    string relative = "maintenance/rollback-" + Guid.NewGuid().ToString("N");
                    string path = MaintenanceFiles.Resolve(Root, relative);
                    if (!CreatePreparationDirectory(path)) throw new IOException("The rollback directory already exists.");
                    _preparation = _preparation with { RollbackDirectory = new(relative, PocketTtsModelStore.DirectoryIdentity(path, Root)) };
                }
                string backupRelative = _preparation.RollbackDirectory.RelativePath + "/" + _preparation.BackupFiles!.Length + ".bin";
                string backupPath = MaintenanceFiles.Resolve(Root, backupRelative);
                bool created = false;
                try
                {
                    using var source = File.OpenRead(MaintenanceFiles.Resolve(Root, file.Path));
                    if (PocketTtsModelStore.FileIdentity(source.Name, source.SafeFileHandle, Root) != original.Identity)
                        throw new IOException("A file changed before its installation backup could be created.");
                    using var output = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    created = true;
                    byte[] buffer = new byte[131072];
                    int count;
                    while ((count = await source.ReadAsync(buffer)) != 0)
                    {
                        checkCancellation();
                        await output.WriteAsync(buffer.AsMemory(0, count));
                    }
                    output.Flush(flushToDisk: true);
                }
                finally
                {
                    if (created)
                    {
                        backup = await MaintenanceFiles.CaptureAsync(Root, backupRelative);
                        _preparation = _preparation with { BackupFiles = [.. _preparation.BackupFiles!, backup!] };
                    }
                }
                if (backup?.Sha256 != original.Sha256) throw new IOException("An installation backup failed verification.");
            }
            _preparation = _preparation with { Payload = [.. _preparation.Payload!, new(file.Path, file.Sha256, original, backup)] };
        }
        checkCancellation();
    }

    private async Task VerifyPreviousReceiptAsync(PreparationRecord record)
    {
        if (await MaintenanceFiles.CaptureAsync(Root, ReceiptPath) == record.PreviousReceipt) return;
        // A retry may follow a completed file restore and receipt refresh.
        if (record.PreviousInstallation is null) throw new InvalidDataException("The installed receipt changed during setup.");
        var expected = await RestoredReceiptAsync(record);
        var actual = await MaintenanceFiles.ReadAsync<InstallationReceipt>(Root, ReceiptPath);
        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(expected))
            throw new InvalidDataException("The installation changed after preparation. Its files were preserved.");
    }

    private async Task RestorePayloadAsync(PreparationRecord record)
    {
        if (record.Payload is null) throw new InvalidDataException("The payload rollback record is missing.");
        foreach (var file in record.Payload)
        {
            var actual = await MaintenanceFiles.CaptureAsync(Root, file.RelativePath);
            if (file.Original is not null && actual?.Sha256 == file.Original.Sha256) continue;
            if (actual is not null && !actual.Sha256.Equals(file.CandidateSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A file changed after installation. Its backup was retained: " + file.RelativePath);
            if (file.Original is null)
            {
                if (actual is not null) Delete(actual);
                continue;
            }
            if (file.Backup is null) throw new InvalidDataException("An original file's rollback backup is missing.");
            var backup = await MaintenanceFiles.CaptureAsync(Root, file.Backup.RelativePath, file.Original.Sha256);
            if (backup != file.Backup) throw new IOException("An installation backup changed. Recovery records were retained.");
            string destination = MaintenanceFiles.Resolve(Root, file.RelativePath);
            string temporaryRelative = record.RollbackDirectory!.RelativePath + "/restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            string temporary = MaintenanceFiles.Resolve(Root, temporaryRelative);
            File.Copy(MaintenanceFiles.Resolve(Root, file.Backup.RelativePath), temporary, overwrite: false);
            try
            {
                _ = await MaintenanceFiles.CaptureAsync(Root, temporaryRelative, file.Original.Sha256);
                using var pins = PocketTtsModelStore.PinParentDirectories(destination);
                if (actual is not null) Delete(actual);
                File.Move(temporary, destination);
            }
            finally
            {
                var leftover = await MaintenanceFiles.CaptureAsync(Root, temporaryRelative);
                if (leftover is not null) Delete(leftover);
            }
        }
        if (record.PreviousInstallation is not null)
            await MaintenanceFiles.WriteAsync(Root, ReceiptPath, await RestoredReceiptAsync(record));
    }

    private async Task<InstallationReceipt> RestoredReceiptAsync(PreparationRecord record)
    {
        var previous = record.PreviousInstallation!;
        var restored = record.Payload!.Where(file => file.Original is not null)
            .ToDictionary(file => file.RelativePath, file => file.Original!, StringComparer.OrdinalIgnoreCase);
        async Task<OwnedFile[]> RefreshAsync(OwnedFile[] files)
        {
            var result = new List<OwnedFile>();
            foreach (var file in files)
                result.Add(restored.TryGetValue(file.RelativePath, out var original) && original.Sha256 == file.Sha256
                    ? await MaintenanceFiles.CaptureAsync(Root, file.RelativePath, file.Sha256)
                        ?? throw new IOException("An original installation file could not be restored.")
                    : file);
            return result.ToArray();
        }
        return previous with { Files = await RefreshAsync(previous.Files), PendingObsolete = await RefreshAsync(previous.PendingObsolete) };
    }

    private void PrunePayloadDirectories(PreparationRecord record)
    {
        var previous = (record.ExistingDirectories ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in record.Payload ?? [])
            for (string? parent = Path.GetDirectoryName(MaintenanceFiles.Resolve(Root, file.RelativePath));
                parent is not null && PocketTtsRuntimeStore.IsWithin(parent, Root); parent = Path.GetDirectoryName(parent))
                directories.Add(Path.GetRelativePath(Root, parent));
        directories.Remove("maintenance");
        foreach (string relative in directories.OrderByDescending(path => path.Length))
        {
            string path = MaintenanceFiles.Resolve(Root, relative);
            if (!previous.Contains(relative) && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                PocketTtsModelStore.DeleteEmptyDirectory(path, PocketTtsModelStore.DirectoryIdentity(path, Root), Root);
        }
    }

    private async Task CleanupRollbackFilesAsync(PreparationRecord record)
    {
        if (record.RollbackDirectory is not { } directory) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(directory.RelativePath, @"\Amaintenance/rollback-[0-9a-f]{32}\z") ||
            (record.BackupFiles ?? []).Any(file => !file.RelativePath.StartsWith(directory.RelativePath + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid rollback backup ownership.");
        foreach (var file in record.BackupFiles ?? []) MaintenanceFiles.Validate(file);
        string path = MaintenanceFiles.Resolve(Root, directory.RelativePath);
        if (!Directory.Exists(path)) return;
        if (PocketTtsModelStore.DirectoryIdentity(path, Root) != directory.Identity)
            throw new IOException("The rollback directory changed. Its files were preserved.");
        string stateRelative = directory.RelativePath + "/state.json";
        var state = await MaintenanceFiles.CaptureAsync(Root, stateRelative);
        if (state is not null)
        {
            var saved = await MaintenanceFiles.ReadAsync<PreparationRecord>(Root, stateRelative);
            if (JsonSerializer.Serialize(saved) != JsonSerializer.Serialize(record))
                throw new IOException("The rollback state changed. Its files were preserved.");
        }
        foreach (var file in record.BackupFiles ?? []) Delete(file);
        if (state is not null) Delete(state);
        if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("Unrecognized rollback files were preserved: " + path);
        PocketTtsModelStore.DeleteEmptyDirectory(path, directory.Identity, Root);
    }

    internal async Task CompletePreparationAsync(string recordPath)
    {
        if (!File.Exists(recordPath)) return;
        string root = Path.GetDirectoryName(recordPath)!;
        var record = await MaintenanceFiles.ReadAsync<PreparationRecord>(root, Path.GetFileName(recordPath));
        if (record.SchemaVersion != 1 || record.Root != Root || record.AppId != MaintenanceFiles.AppId) throw new InvalidDataException("Invalid completed preparation record.");
        await CleanupRollbackFilesAsync(record);
    }
}
