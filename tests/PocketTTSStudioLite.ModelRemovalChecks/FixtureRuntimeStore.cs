using System.Text.Json;

namespace PocketTTSStudioLite.Services;

// Only infrastructure is substituted: linked production removal/inventory/Win32 code uses a temporary sandbox.
// The production runtime store has no override and still fixes models under LocalApplicationData.
internal static class PocketTtsRuntimeStore
{
    internal static string LocalDataPath { get; set; } = "";
    internal static void EnsureDirectory(string path) => Directory.CreateDirectory(path);
    internal static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }
}

public sealed record PocketTtsModelInstallProgress(string Message, double? Percent = null, bool CanCancel = true);
