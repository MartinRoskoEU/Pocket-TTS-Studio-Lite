using System.Reflection;

namespace PocketTTSStudioLite.Services;

internal static class ApplicationInfo
{
    internal static string Version => typeof(ApplicationInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    internal static string UserAgent => "PocketTTSStudioLite/" + Version;
}
