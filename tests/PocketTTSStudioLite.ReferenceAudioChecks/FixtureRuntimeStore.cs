namespace PocketTTSStudioLite.Services;

// This test-only substitute isolates actual production audio code from the user's application data.
internal static class PocketTtsRuntimeStore
{
    internal static string LocalDataPath { get; set; } = "";
}
