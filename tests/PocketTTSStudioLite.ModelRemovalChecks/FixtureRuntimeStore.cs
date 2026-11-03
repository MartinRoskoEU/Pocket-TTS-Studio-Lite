using System.Text.Json;

namespace PocketTTSStudioLite.Services;

// Only infrastructure is substituted: linked production removal/inventory/Win32 code uses a temporary sandbox.
// The production runtime store has no override and still fixes models under LocalApplicationData.
internal static class PocketTtsRuntimeStore
{
    internal static string LocalDataPath { get; set; } = "";
    internal static string RuntimesPath => Path.Combine(LocalDataPath, "runtimes");
    internal static int Generation { get; set; }
    internal static string ResolveActivePath() => Path.Combine(LocalDataPath, "fixture-runtime.exe");
    internal sealed record InactiveCleanup(long DeletedBytes, bool HadWork, bool CleanupPending);
    internal static Task<InactiveCleanup> CleanupInactiveAsync(CancellationToken token) => Task.FromResult(new InactiveCleanup(0, false, false));
    internal static Task<bool> ReadVerifiedActiveAsync(CancellationToken token) => Task.FromResult(true);
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

// Runtime diagnostics alone are substituted; updates still execute production downloads,
// validation, inventory commit, leases, receipts and checked cleanup on fixture files.
internal sealed record PocketTtsCliService(string Path);
internal static class PocketTtsModelSetupService
{
    internal sealed record RuntimeIdentity(string ReleaseVersion, string ExecutableSha256);
    internal static Task<RuntimeIdentity> ReadVerifiedRuntimeIdentityAsync(CancellationToken token) =>
        Task.FromResult(new RuntimeIdentity("fixture-" + PocketTtsRuntimeStore.Generation, new string('a', 64)));
    internal static Task<string[]> ValidateCliAsync(PocketTtsCliService cli, bool bundled, CancellationToken token) =>
        Task.FromResult(new[] { "english", "french" });
}
