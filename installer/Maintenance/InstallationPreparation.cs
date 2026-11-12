using PocketTTSStudioLite.Services;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PocketTTSStudioLite.Maintenance;

internal sealed partial class InstallationMaintenance
{
    private sealed record PreparationRecord(int SchemaVersion, string AppId, string Root, string PackageHash,
        string RootIdentity, bool RootCreated, string? MaintenanceIdentity, bool MaintenanceCreated,
        OwnedFile? Plan, OwnedFile? PreviousReceipt,
        PreparedPayloadFile[]? Payload = null, string[]? ExistingDirectories = null,
        OwnedDirectory? RollbackDirectory = null, OwnedFile[]? BackupFiles = null,
        InstallationReceipt? PreviousInstallation = null);

    private PreparationRecord? _preparation;

    private bool CreatePreparationDirectory(string path)
    {
        PocketTtsModelStore.ValidatePath(path, ownedRoot: Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (CreateDirectory(path, IntPtr.Zero)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error != 183 || !Directory.Exists(path))
            throw new IOException("The preparation directory could not be created.", new Win32Exception(error));
        PocketTtsModelStore.ValidatePath(path, ownedRoot: Root);
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);

    internal async Task PrepareTrackedAsync(string packageHash, PackageFile[] files, string recordPath, Action checkCancellation)
    {
        if (Path.Exists(recordPath)) throw new InvalidOperationException("Previous preparation cleanup must finish before retrying.");
        try
        {
            await PrepareAsync(packageHash, files, checkCancellation);
            await BackupPayloadAsync(packageHash, files, checkCancellation);
        }
        finally
        {
            if (_preparation is not null)
            {
                if (_preparation.RollbackDirectory is { } backupDirectory)
                    await MaintenanceFiles.WriteAsync(Root, backupDirectory.RelativePath + "/state.json", _preparation);
                await PocketTtsRuntimeStore.WriteJsonAsync(recordPath, _preparation, default);
            }
        }
    }

    internal async Task CancelPreparationAsync(string packageHash, string recordPath, bool rollbackPayload = false, PackageFile[]? packageFiles = null)
    {
        string recordRoot = Path.GetDirectoryName(recordPath)!;
        var recordFile = await MaintenanceFiles.CaptureAsync(recordRoot, Path.GetFileName(recordPath));
        if (recordFile is null) return;
        var record = await MaintenanceFiles.ReadAsync<PreparationRecord>(recordRoot, Path.GetFileName(recordPath));
        if (record.SchemaVersion != 1 || record.AppId != MaintenanceFiles.AppId || record.Root != Root || record.PackageHash != packageHash ||
            (record.Plan is not null && record.Plan.RelativePath != UpgradePath))
            throw new InvalidDataException("Preparation cleanup ownership could not be verified.");
        if (rollbackPayload)
        {
            var expected = (packageFiles ?? throw new InvalidDataException("The rollback package inventory is missing."))
                .Append(new PackageFile("maintenance/package.json", packageHash)).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            if (record.Payload is null || record.Payload.Length != expected.Count ||
                record.Payload.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Count ||
                record.Payload.Any(file => !expected.TryGetValue(file.RelativePath, out var candidate) ||
                    !candidate.Sha256.Equals(file.CandidateSha256, StringComparison.OrdinalIgnoreCase) ||
                    (file.Original is not null && (file.Original.RelativePath != file.RelativePath || file.Backup is null ||
                        file.Backup.Sha256 != file.Original.Sha256 || !(record.BackupFiles ?? []).Contains(file.Backup)))))
                throw new InvalidDataException("The rollback payload differs from the verified package inventory.");
        }
        if (record.PreviousInstallation is not null) ValidateReceipt(record.PreviousInstallation);
        if (record.RollbackDirectory is { } backupDirectory)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(backupDirectory.RelativePath, @"\Amaintenance/rollback-[0-9a-f]{32}\z") ||
                (record.BackupFiles ?? []).Any(file => !file.RelativePath.StartsWith(backupDirectory.RelativePath + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("The rollback backup paths are invalid.");
            foreach (var file in record.BackupFiles ?? []) MaintenanceFiles.Validate(file);
        }

        if (Directory.Exists(Root))
        {
            if (PocketTtsModelStore.DirectoryIdentity(Root, Root) != record.RootIdentity)
                throw new InvalidDataException("The preparation directory changed. It was preserved.");
            string maintenance = MaintenanceFiles.Resolve(Root, "maintenance");
            if (record.MaintenanceIdentity is not null && Directory.Exists(maintenance))
            {
                using (var pins = PocketTtsModelStore.PinParentDirectories(Path.Combine(maintenance, "operation.lock")))
                using (var operationLock = new FileStream(Path.Combine(maintenance, "operation.lock"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
                {
                    PocketTtsModelStore.ValidateFile(operationLock.Name, operationLock.SafeFileHandle, ownedRoot: Root);
                    if (PocketTtsModelStore.DirectoryIdentity(maintenance, Root) != record.MaintenanceIdentity)
                        throw new InvalidDataException("The maintenance directory changed. Its files were preserved.");
                    await VerifyPreviousReceiptAsync(record);
                    if (rollbackPayload) await RestorePayloadAsync(record);
                    if (record.Plan is not null)
                    {
                        MaintenanceFiles.Validate(record.Plan);
                        Delete(record.Plan);
                    }
                }
                await CleanupRollbackFilesAsync(record);
                if (rollbackPayload) PrunePayloadDirectories(record);
                if (record.MaintenanceCreated && !Directory.EnumerateFileSystemEntries(maintenance).Any())
                    PocketTtsModelStore.DeleteEmptyDirectory(maintenance, record.MaintenanceIdentity, Root);
            }
            if (record.RootCreated && !Directory.EnumerateFileSystemEntries(Root).Any())
                PocketTtsModelStore.DeleteEmptyDirectory(Root, record.RootIdentity, Root);
        }
        PocketTtsModelStore.DeleteRegularFile(recordPath, expectedIdentity: recordFile.Identity,
            expectedSha256: recordFile.Sha256, ownedRoot: recordRoot);
    }
}
