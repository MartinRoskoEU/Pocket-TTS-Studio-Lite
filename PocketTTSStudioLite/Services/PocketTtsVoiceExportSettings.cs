using System.Text.Json;

namespace PocketTTSStudioLite.Services;

/// <summary>Voice Export preferences use the application's local, versioned JSON settings convention.</summary>
internal sealed class PocketTtsVoiceExportSettings
{
    private sealed record Settings(int SchemaVersion, string ExportFolder);
    private readonly string _path;

    internal PocketTtsVoiceExportSettings() : this(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "voice-export.json")) { }
    internal PocketTtsVoiceExportSettings(string path) => _path = path;

    internal async Task<string?> ReadFolderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > 65536) { throw new InvalidDataException("Saved export folder settings are too large."); }
            var settings = await JsonSerializer.DeserializeAsync<Settings>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (settings is null || settings.SchemaVersion != 1)
            { throw new InvalidDataException("Saved export folder settings are invalid or incompatible."); }
            // Read preferences independently of availability: a missing folder must never be recreated.
            return LocalFolder(settings.ExportFolder);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal Task SaveFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        string fullPath = LocalFolder(folder);
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".voice-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await PocketTtsRuntimeStore.WriteJsonAsync(temporary, new Settings(1, fullPath), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path)) { File.Replace(temporary, _path, null); }
            else { File.Move(temporary, _path); }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }, cancellationToken);

    internal static Task ValidateFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = LocalFolder(folder);
        if (!Directory.Exists(fullPath)) { throw new DirectoryNotFoundException("The export folder is unavailable. Choose an accessible local folder."); }
        // Verify create/write/delete access without retaining a file or touching an existing profile.
        string probe = Path.Combine(fullPath, $".PocketTTSStudioLite-folder-{Guid.NewGuid():N}.tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken);

    private static string LocalFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
        { throw new ArgumentException("Choose a local export folder on this computer.", nameof(folder)); }
        return Path.GetFullPath(folder);
    }
}
