namespace PocketTTSStudioLite.Services;

// Only platform/CLI diagnostics and the private root are substituted. Runtime storage,
// identity activation, receipts, HTTP, ZIP/PE checks, leases and deletion are production code.
internal static class PocketTtsModelSetupService
{
    internal static string Root { get; set; } = "";
    internal static string RuntimeIdentityPath => Path.Combine(Root, "runtime-identity.json");
    internal const string ValidatedRuntimeRelease = "v4.0.1";
    internal static string ValidatedRuntimeSha256 { get; set; } = new('a', 64);
    internal sealed record RuntimeIdentity(string ReleaseVersion, string ExecutableSha256);
    internal static async Task<RuntimeIdentity> ReadVerifiedRuntimeIdentityAsync(CancellationToken token) =>
        (await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(token)).Identity;
    internal static Task<string[]> ValidateCliAsync(PocketTtsCliService cli, bool bundled, CancellationToken token) =>
        Task.FromResult(new[] { "english", "french" });
}

internal sealed record PocketTtsCliService(string Path)
{
    internal static void EnsureSupportedProcessor() { }
}
