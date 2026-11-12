using PocketTTSStudioLite.Services;
using System.Text.Json;

namespace PocketTTSStudioLite.Maintenance;

internal sealed record PackageFile(string Path, string Sha256);
internal sealed record PackageManifest(int SchemaVersion, string AppId, string Version, PackageFile[] Files);
internal sealed record InstallationReceipt(int SchemaVersion, string AppId, string RootIdentity, string PackageHash,
    OwnedFile[] Files, OwnedFile[] PendingObsolete);
internal sealed record UpgradePlan(int SchemaVersion, string RootIdentity, string PackageHash, OwnedFile[] PreviousFiles);

internal sealed partial class InstallationMaintenance(string appRoot)
{
    internal const string ReceiptPath = "maintenance/installation.json", UpgradePath = "maintenance/upgrade-plan.json";
    internal string Root { get; } = ValidateRoot(appRoot);
    internal bool LegacyFilesPreserved { get; private set; }

    private static string ValidateRoot(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root.Length < 4 || root[1] != ':' || root[2] != '\\' || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Choose a dedicated directory on a local Windows drive.");
        string data = PocketTtsRuntimeStore.LocalDataPath;
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) || root.Equals(data, StringComparison.OrdinalIgnoreCase) ||
            PocketTtsRuntimeStore.IsWithin(root, data) || PocketTtsRuntimeStore.IsWithin(data, root))
            throw new InvalidDataException("The application and private-data roots must be separate, dedicated directories.");
        PocketTtsModelStore.ValidatePath(root, ownedRoot: root);
        return root;
    }

    internal static async Task<PackageManifest> ReadPackageAsync(string path, string expectedHash)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        path = MaintenanceFiles.Resolve(root, Path.GetFileName(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PocketTtsModelStore.ValidateFile(path, stream.SafeFileHandle, ownedRoot: root);
        if (stream.Length is <= 0 or > 16 * 1024 * 1024 || !Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package manifest integrity could not be verified.");
        stream.Position = 0;
        var package = await JsonSerializer.DeserializeAsync<PackageManifest>(stream, new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Invalid package inventory.");
        if (package.SchemaVersion != 1 || package.AppId != MaintenanceFiles.AppId || package.Files is not { Length: > 0 and <= 4096 } || package.Files.Any(file => file is null) ||
            package.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Files.Length ||
            !Version.TryParse(package.Version, out _)) throw new InvalidDataException("Unsupported package inventory.");
        foreach (var file in package.Files)
        {
            _ = MaintenanceFiles.Resolve(root, file.Path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(file.Sha256 ?? "", @"\A[a-fA-F0-9]{64}\z")) throw new InvalidDataException("Invalid package hash.");
            if (file.Path.StartsWith("maintenance/installation", StringComparison.OrdinalIgnoreCase) || file.Path == UpgradePath ||
                file.Path.Contains("uninstall-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package inventory contains generated maintenance state.");
        }
        return package;
    }

    private void ValidateReceipt(InstallationReceipt receipt)
    {
        if (receipt.SchemaVersion != 1 || receipt.AppId != MaintenanceFiles.AppId || receipt.RootIdentity != PocketTtsModelStore.DirectoryIdentity(Root, Root) ||
            receipt.Files is null || receipt.PendingObsolete is null || receipt.Files.Any(file => file is null) || receipt.PendingObsolete.Any(file => file is null) || receipt.Files.Length + receipt.PendingObsolete.Length > 8192 ||
            receipt.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.Files.Length ||
            receipt.PendingObsolete.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.PendingObsolete.Length ||
            receipt.Files.Select(file => file.RelativePath).Intersect(receipt.PendingObsolete.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase).Any() ||
            !System.Text.RegularExpressions.Regex.IsMatch(receipt.PackageHash ?? "", @"\A[a-fA-F0-9]{64}\z")) throw new InvalidDataException("Installation ownership could not be verified.");
        foreach (var file in receipt.Files.Concat(receipt.PendingObsolete)) { MaintenanceFiles.Validate(file); _ = MaintenanceFiles.Resolve(Root, file.RelativePath); }
    }

    internal async Task PrepareAsync(string packageHash, PackageFile[]? candidateFiles = null, Action? checkCancellation = null)
    {
        if (candidateFiles is not null)
            foreach (var file in candidateFiles)
            {
                checkCancellation?.Invoke();
                _ = await MaintenanceFiles.CaptureAsync(Root, file.Path);
            }
        _ = await MaintenanceFiles.CaptureAsync(Root, "maintenance/package.json");
        if (File.Exists(MaintenanceFiles.Resolve(Root, "maintenance/uninstall-state.ini")) || File.Exists(MaintenanceFiles.Resolve(Root, "maintenance/uninstall-plan.json")))
            throw new InvalidOperationException("Finish the interrupted uninstall before reinstalling.");
        bool rootCreated = CreatePreparationDirectory(Root);
        string identity = PocketTtsModelStore.DirectoryIdentity(Root, Root);
        _preparation = new(1, MaintenanceFiles.AppId, Root, packageHash, identity, rootCreated,
            null, false, null, null);
        _preparation = _preparation with { PreviousReceipt = await MaintenanceFiles.CaptureAsync(Root, ReceiptPath) };
        if (File.Exists(MaintenanceFiles.Resolve(Root, UpgradePath)))
        {
            var pending = await MaintenanceFiles.ReadAsync<UpgradePlan>(Root, UpgradePath);
            if (pending.SchemaVersion != 1 || pending.RootIdentity != identity || pending.PackageHash != packageHash || pending.PreviousFiles is null || pending.PreviousFiles.Length > 8192)
                throw new InvalidOperationException("Resume the interrupted setup using the same package.");
            foreach (var file in pending.PreviousFiles) { MaintenanceFiles.Validate(file); _ = MaintenanceFiles.Resolve(Root, file.RelativePath); }
            return;
        }
        OwnedFile[] previous = [];
        if (File.Exists(MaintenanceFiles.Resolve(Root, ReceiptPath)))
        {
            var receipt = await MaintenanceFiles.ReadAsync<InstallationReceipt>(Root, ReceiptPath); ValidateReceipt(receipt);
            foreach (var file in receipt.Files.Concat(receipt.PendingObsolete))
            {
                checkCancellation?.Invoke();
                var actual = await MaintenanceFiles.CaptureAsync(Root, file.RelativePath, file.Sha256);
                if (actual is not null && actual.Identity != file.Identity) throw new InvalidDataException("An installed file was replaced. It was preserved.");
            }
            previous = receipt.Files.Concat(receipt.PendingObsolete).DistinctBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else LegacyFilesPreserved = Directory.EnumerateFileSystemEntries(Root).Any();
        checkCancellation?.Invoke();
        string maintenanceDirectory = MaintenanceFiles.Resolve(Root, "maintenance");
        bool maintenanceCreated = CreatePreparationDirectory(maintenanceDirectory);
        _preparation = _preparation with
        {
            MaintenanceIdentity = PocketTtsModelStore.DirectoryIdentity(maintenanceDirectory, Root),
            MaintenanceCreated = maintenanceCreated,
        };
        await MaintenanceFiles.WriteAsync(Root, UpgradePath, new UpgradePlan(1, identity, packageHash, previous), replace: false);
        _preparation = _preparation with { Plan = await MaintenanceFiles.CaptureAsync(Root, UpgradePath) };
    }

    internal async Task<bool> CommitAsync(PackageManifest package, string packageHash)
    {
        var plan = await MaintenanceFiles.ReadAsync<UpgradePlan>(Root, UpgradePath);
        if (plan.SchemaVersion != 1 || plan.RootIdentity != PocketTtsModelStore.DirectoryIdentity(Root, Root) || plan.PackageHash != packageHash || plan.PreviousFiles is null)
            throw new InvalidDataException("Setup ownership changed.");
        foreach (var file in plan.PreviousFiles) { MaintenanceFiles.Validate(file); _ = MaintenanceFiles.Resolve(Root, file.RelativePath); }
        var files = new List<OwnedFile>();
        foreach (var file in package.Files)
            files.Add(await MaintenanceFiles.CaptureAsync(Root, file.Path, file.Sha256) ?? throw new InvalidDataException("Installed payload is incomplete: " + file.Path));
        // The package manifest is intentionally not self-referential. Bind its own
        // installed bytes separately when present (the installer embeds its hash).
        if (File.Exists(MaintenanceFiles.Resolve(Root, "maintenance/package.json")))
            files.Add((await MaintenanceFiles.CaptureAsync(Root, "maintenance/package.json", packageHash))!);
        var paths = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = plan.PreviousFiles.Where(file => !paths.Contains(file.RelativePath)).ToArray();
        if (files.Count + obsolete.Length > 8192) throw new InvalidDataException("Installation cleanup exceeds its supported inventory limit.");
        var receipt = new InstallationReceipt(1, MaintenanceFiles.AppId, plan.RootIdentity, packageHash, files.ToArray(), obsolete);
        await MaintenanceFiles.WriteAsync(Root, ReceiptPath, receipt);
        bool complete = await CleanupObsoleteAsync(receipt);
        var saved = await MaintenanceFiles.CaptureAsync(Root, UpgradePath);
        if (saved is not null) Delete(saved);
        return complete;
    }

    internal async Task<bool> CleanupObsoleteAsync(InstallationReceipt receipt)
    {
        ValidateReceipt(receipt);
        var pending = new List<OwnedFile>();
        foreach (var file in receipt.PendingObsolete)
        {
            try { Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { pending.Add(file); }
        }
        await MaintenanceFiles.WriteAsync(Root, ReceiptPath, receipt with { PendingObsolete = pending.ToArray() });
        return pending.Count == 0;
    }

    private void Delete(OwnedFile file) => PocketTtsModelStore.DeleteRegularFile(MaintenanceFiles.Resolve(Root, file.RelativePath),
        expectedIdentity: file.Identity, expectedSha256: file.Sha256, ownedRoot: Root);

    internal async Task UninstallAsync(string packageHash, bool removePrivate)
    {
        var receipt = await MaintenanceFiles.ReadAsync<InstallationReceipt>(Root, ReceiptPath); ValidateReceipt(receipt);
        if (receipt.PackageHash != packageHash) throw new InvalidDataException("Use this installation's matching uninstaller.");
        if (!await CleanupObsoleteAsync(receipt)) throw new IOException("Obsolete owned payload cleanup is pending. Close applications using these files and retry.");
        if (removePrivate) await new PrivateDataCleanup(Root).RunAsync();
        string state = "[state]\r\nschema=1\r\napp_id=" + MaintenanceFiles.AppId + "\r\napp_root=" + Root + "\r\napp_identity=" + receipt.RootIdentity +
            "\r\npackage_hash=" + packageHash + "\r\nremove_private=" + (removePrivate ? "1" : "0") + "\r\ndata_complete=1\r\n[metadata]\r\n";
        foreach (string relative in new[] { ReceiptPath, UpgradePath, "maintenance/uninstall-plan.json" })
        {
            var file = await MaintenanceFiles.CaptureAsync(Root, relative);
            if (file is not null) state += Path.GetFileName(relative) + "=" + file.Sha256 + "\r\n";
        }
        string path = MaintenanceFiles.Resolve(Root, "maintenance/uninstall-state.ini");
        if (File.Exists(path)) throw new InvalidOperationException("Uninstall state already exists; resume it using the uninstaller.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(state)).ToArray();
        await output.WriteAsync(bytes); output.Flush(true);
    }
}
