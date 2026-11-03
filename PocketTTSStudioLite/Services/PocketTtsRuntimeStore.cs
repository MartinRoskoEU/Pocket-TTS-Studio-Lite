using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PocketTTSStudioLite.Services;

/// <summary>Local runtime selection, installation receipts, and atomic identity activation.</summary>
internal static class PocketTtsRuntimeStore
{
    internal static string LocalDataPath => Path.GetDirectoryName(PocketTtsModelSetupService.RuntimeIdentityPath)!;
    internal static string RuntimesPath => Path.Combine(LocalDataPath, "runtimes");
    internal static string StagingPath => Path.Combine(RuntimesPath, ".staging");
    internal static string BundledPath => Path.Combine(AppContext.BaseDirectory, "runtime", "pocket-tts", "pocket-tts.exe");
    internal sealed record VerifiedRuntime(PocketTtsModelSetupService.RuntimeIdentity Identity, string ExecutablePath);
    internal sealed record InstallationReceipt(string ReleaseVersion, string ExecutableSha256, string ArchiveSha256,
        long AssetId, string AssetName, string AssetUrl, string LicenseSha256);

    internal static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z");

    internal static string ReleasePath(string tag, string digest)
    {
        PocketTtsRuntimeUpdateService.ValidateReleaseTag(tag);
        if (tag.Length > 128 || !IsHash(digest)) { throw new InvalidDataException("Invalid runtime installation identity."); }
        return Path.Combine(RuntimesPath, tag, digest.ToUpperInvariant());
    }

    internal static string ResolveActivePath()
    {
        string identityPath = PocketTtsModelSetupService.RuntimeIdentityPath;
        if (!File.Exists(identityPath)) { return BundledPath; }
        var identity = ReadJson<PocketTtsModelSetupService.RuntimeIdentity>(identityPath);
        return PathForIdentity(identity);
    }

    private static string PathForIdentity(PocketTtsModelSetupService.RuntimeIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.ReleaseVersion) || !IsHash(identity.ExecutableSha256))
        { throw new InvalidDataException("The saved runtime identity is invalid."); }
        if (identity.ReleaseVersion == PocketTtsModelSetupService.ValidatedRuntimeRelease)
        {
            if (!string.Equals(identity.ExecutableSha256, PocketTtsModelSetupService.ValidatedRuntimeSha256, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("The saved bundled runtime identity does not match the trusted SHA-256."); }
            return BundledPath;
        }
        return Path.Combine(ReleasePath(identity.ReleaseVersion, identity.ExecutableSha256), "pocket-tts.exe");
    }

    internal static async Task<VerifiedRuntime> ReadVerifiedActiveAsync(CancellationToken cancellationToken, bool initializeIdentity = false)
    {
        if (initializeIdentity && !File.Exists(PocketTtsModelSetupService.RuntimeIdentityPath))
        { return await VerifyRuntimeAsync(BundledPath, cancellationToken).ConfigureAwait(false); }
        var identity = ReadJson<PocketTtsModelSetupService.RuntimeIdentity>(PocketTtsModelSetupService.RuntimeIdentityPath);
        string path = PathForIdentity(identity);
        var verified = await VerifyRuntimeAsync(path, cancellationToken, initializeIdentity: false).ConfigureAwait(false);
        if (verified.Identity.ReleaseVersion != identity.ReleaseVersion ||
            !string.Equals(verified.Identity.ExecutableSha256, identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("The active runtime does not match its saved identity."); }
        return verified;
    }

    internal static async Task<VerifiedRuntime> VerifyRuntimeAsync(string path, CancellationToken cancellationToken, bool initializeIdentity = true)
    {
        string fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidDataException("Runtime executables cannot be file links or reparse points."); }
        string digest = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!IsWithin(fullPath, RuntimesPath))
        {
            if (digest != PocketTtsModelSetupService.ValidatedRuntimeSha256)
            { throw new InvalidDataException("This runtime does not match the trusted bundled Pocket TTS v4.0.1 executable."); }
            var identity = new PocketTtsModelSetupService.RuntimeIdentity(PocketTtsModelSetupService.ValidatedRuntimeRelease, digest);
            if (initializeIdentity && !File.Exists(PocketTtsModelSetupService.RuntimeIdentityPath))
            {
                EnsureDirectory(LocalDataPath);
                await WriteIdentityAsync(identity, replace: false, cancellationToken).ConfigureAwait(false);
            }
            // A valid stored update must not be overwritten when explicitly checking the fallback.
            if (File.Exists(PocketTtsModelSetupService.RuntimeIdentityPath))
            { _ = PathForIdentity(ReadJson<PocketTtsModelSetupService.RuntimeIdentity>(PocketTtsModelSetupService.RuntimeIdentityPath)); }
            return new(identity, fullPath);
        }

        EnsureNoReparsePoints(Path.GetDirectoryName(fullPath)!);
        var receipt = ReadJson<InstallationReceipt>(Path.Combine(Path.GetDirectoryName(fullPath)!, "runtime-installation.json"));
        if (string.IsNullOrWhiteSpace(receipt.ReleaseVersion) || !IsHash(receipt.ExecutableSha256) ||
            !IsHash(receipt.ArchiveSha256) || !IsHash(receipt.LicenseSha256) ||
            string.IsNullOrWhiteSpace(receipt.AssetName) || string.IsNullOrWhiteSpace(receipt.AssetUrl))
        { throw new InvalidDataException("The runtime installation receipt is invalid."); }
        string expectedPath = Path.Combine(ReleasePath(receipt.ReleaseVersion, receipt.ExecutableSha256), "pocket-tts.exe");
        if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(digest, receipt.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            receipt.AssetId <= 0 ||
            !PocketTtsRuntimeUpdateService.IsOfficialAssetUrl(new Uri(receipt.AssetUrl), receipt.ReleaseVersion, receipt.AssetName))
        { throw new InvalidDataException("The installed runtime does not match its verified installation receipt."); }
        string licenseHash = await HashFileAsync(Path.Combine(Path.GetDirectoryName(fullPath)!, "LICENSE"), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(licenseHash, receipt.LicenseSha256, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("The runtime license does not match its installation receipt."); }
        return new(new(receipt.ReleaseVersion, digest), fullPath);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    internal static T ReadJson<T>(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 16 * 1024) { throw new InvalidDataException("Runtime identity metadata is too large."); }
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Runtime identity metadata is invalid.");
    }

    internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteIdentityAsync(PocketTtsModelSetupService.RuntimeIdentity identity, bool replace, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(LocalDataPath, $".runtime-identity-{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteJsonAsync(temporary, identity, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (replace)
            { File.Replace(temporary, PocketTtsModelSetupService.RuntimeIdentityPath,
                Path.Combine(LocalDataPath, "runtime-identity.previous.json")); }
            else
            {
                try { File.Move(temporary, PocketTtsModelSetupService.RuntimeIdentityPath); }
                catch (IOException) when (File.Exists(PocketTtsModelSetupService.RuntimeIdentityPath))
                { _ = PathForIdentity(ReadJson<PocketTtsModelSetupService.RuntimeIdentity>(PocketTtsModelSetupService.RuntimeIdentityPath)); }
            }
        }
        finally
        {
            // A cleanup error must not turn a successful atomic commit into an apparent activation failure.
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task ActivateAsync(VerifiedRuntime previous, string executablePath, CancellationToken cancellationToken)
    {
        var candidate = await VerifyRuntimeAsync(executablePath, cancellationToken, initializeIdentity: false).ConfigureAwait(false);
        var current = await ReadVerifiedActiveAsync(cancellationToken).ConfigureAwait(false);
        if (current.Identity != previous.Identity) { throw new InvalidOperationException("The active runtime changed. Check for updates and confirm again."); }
        cancellationToken.ThrowIfCancellationRequested();
        bool committed = false;
        try
        {
            // Cancellation ends at the atomic commit. A committed update is either verified or rolled back.
            await WriteIdentityAsync(candidate.Identity, replace: true, CancellationToken.None).ConfigureAwait(false);
            committed = true;
            _ = await ReadVerifiedActiveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception activationFailure)
        {
            if (committed)
            {
                try
                {
                    // Restore the exact previous bytes by rename; rollback needs no new temporary-file allocation.
                    File.Replace(Path.Combine(LocalDataPath, "runtime-identity.previous.json"), PocketTtsModelSetupService.RuntimeIdentityPath, null);
                    _ = await ReadVerifiedActiveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                { throw new InvalidOperationException("Runtime activation and identity recovery failed. Previous runtime files are retained; inspect the saved runtime identity and its previous backup before retrying.", new AggregateException(activationFailure, rollbackFailure)); }
            }
            throw new InvalidOperationException("Runtime activation failed. The previous runtime and identity have been retained.", activationFailure);
        }
    }

    internal static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal sealed record InactiveCleanup(long DeletedBytes, bool HadWork, bool CleanupPending);

    // Caller owns runtime-update.lock. Never called by generation or export.
    internal static async Task<InactiveCleanup> CleanupInactiveAsync(CancellationToken token)
    {
        PocketTtsModelStore.ValidatePath(RuntimesPath, localData: true);
        if (!Directory.Exists(RuntimesPath)) return new(0, false, false);
        long deleted = 0;
        bool work = false, pending = false;
        var candidates = new List<string>();
        foreach (string release in Directory.EnumerateDirectories(RuntimesPath))
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetFileName(release) == ".staging") continue;
            try
            {
                PocketTtsModelStore.ValidatePath(release, localData: true);
                foreach (string directory in Directory.EnumerateDirectories(release))
                {
                    if (!IsHash(Path.GetFileName(directory))) continue;
                    try
                    {
                        PocketTtsModelStore.ValidatePath(directory, localData: true);
                        if (File.Exists(Path.Combine(directory, "runtime-installation.json"))) candidates.Add(directory);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    { work = pending = true; }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { work = pending = true; }
        }
        if (candidates.Count == 0) return new(deleted, work, pending);
        string activeDirectory = Path.GetDirectoryName(ResolveActivePath())!;
        candidates = candidates.Where(directory => !string.Equals(directory, activeDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return new(deleted, work, pending);
        var active = await ReadVerifiedActiveAsync(token).ConfigureAwait(false);
        foreach (string directory in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(directory, activeDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            work = true;
            PocketTtsDownloadOperation? retirement = null;
            try
            {
                if ((await ReadVerifiedActiveAsync(token).ConfigureAwait(false)).Identity != active.Identity)
                    throw new InvalidOperationException("The active runtime changed during cleanup.");
                string[] names = ["pocket-tts.exe", "LICENSE", "runtime-installation.json"];
                if (!Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                    .SequenceEqual(names.Order(StringComparer.Ordinal)))
                    throw new InvalidDataException("Unknown old runtime content was preserved.");
                var verified = await VerifyRuntimeAsync(Path.Combine(directory, "pocket-tts.exe"), token, initializeIdentity: false).ConfigureAwait(false);
                var receipt = ReadJson<InstallationReceipt>(Path.Combine(directory, "runtime-installation.json"));
                var leases = new List<FileStream>();
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    foreach (string name in names)
                    {
                        string path = Path.Combine(directory, name);
                        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                        leases.Add(lease);
                        PocketTtsModelStore.ValidateFile(path, lease.SafeFileHandle, localData: true);
                        hashes.Add("payload/" + name, Convert.ToHexString(await SHA256.HashDataAsync(lease, token).ConfigureAwait(false)));
                    }
                    if (!hashes["payload/pocket-tts.exe"].Equals(verified.Identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
                        !hashes["payload/LICENSE"].Equals(receipt.LicenseSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The old runtime changed during cleanup.");
                    leases[2].Position = 0;
                    if (await JsonSerializer.DeserializeAsync<InstallationReceipt>(leases[2], cancellationToken: token).ConfigureAwait(false) != receipt)
                        throw new InvalidDataException("The old runtime receipt changed during cleanup.");
                    retirement = await PocketTtsDownloadOperation.BeginRuntimeRetirementAsync(hashes, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // Windows requires child handles to close for a directory rename. The durable
                    // retirement record rechecks these exact hashes before and during deletion.
                    foreach (var lease in leases) lease.Dispose();
                    leases.Clear();
                    Directory.Move(directory, Path.Combine(retirement.DirectoryPath, "payload"));
                }
                finally { foreach (var lease in leases) lease.Dispose(); }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException)
            { pending = true; }
            finally
            {
                if (retirement is not null)
                {
                    try { deleted += await retirement.CleanupAsync().ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException)
                    { pending = true; }
                }
            }
            string parent = Path.GetDirectoryName(directory)!;
            if (!Directory.Exists(directory))
            {
                try
                {
                    PocketTtsModelStore.ValidatePath(parent, localData: true);
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                        PocketTtsModelStore.DeleteEmptyDirectory(parent, PocketTtsModelStore.DirectoryIdentity(parent));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { /* Empty parent pruning is optional. */ }
            }
        }
        return new(deleted, work, pending);
    }

    internal static void EnsureDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, LocalDataPath, StringComparison.OrdinalIgnoreCase) && !IsWithin(fullPath, LocalDataPath))
        { throw new InvalidDataException("Runtime storage must remain inside local application data."); }
        EnsureNoReparsePoints(fullPath);
        Directory.CreateDirectory(fullPath);
        EnsureNoReparsePoints(fullPath);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidDataException("Runtime storage cannot use directory links or reparse points."); }
            if (string.Equals(directory.FullName, LocalDataPath, StringComparison.OrdinalIgnoreCase)) { break; }
        }
    }

}
