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

    internal static void DeleteStagingDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!IsWithin(fullPath, StagingPath)) { throw new InvalidOperationException("Refusing to delete outside runtime staging."); }
        if (Directory.Exists(fullPath)) { EnsureNoReparsePoints(fullPath); Directory.Delete(fullPath, recursive: true); }
    }
}
