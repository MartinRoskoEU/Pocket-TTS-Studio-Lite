using Microsoft.Win32;

namespace PocketTTSStudioLite.Services;

/// <summary>Installed MF source-handler routes are hints; successful PCM decoding always decides acceptance.</summary>
internal sealed class ReferenceAudioFormats
{
    internal sealed record Route(string? MimeType, string[] Extensions);
    private static readonly string[] CommonExtensions = [".wav", ".mp3", ".flac", ".m4a", ".aac", ".adts", ".wma", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc", ".alac", ".m4b", ".amr", ".ac3", ".ec3", ".mpa", ".mp2", ".mka", ".weba", ".webm", ".mp4", ".asf", ".3gp", ".3g2", ".caf", ".au", ".snd"];
    internal IReadOnlyList<Route> Routes { get; }
    internal IReadOnlyList<string> Extensions { get; }
    internal ReferenceAudioFormats(IReadOnlyList<Route> routes)
    {
        Routes = routes;
        // Shared picker/drop extensions: audio formats and common containers with an audio stream.
        // Successful decoding is still required after extension filtering.
        var registered = routes.SelectMany(route => route.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<Guid, bool>();
        bool Has(Guid subtype)
        {
            if (!cache.TryGetValue(subtype, out bool found)) { cache[subtype] = found = MediaFoundationAudioDecoder.HasDecoder(subtype); }
            return found;
        }
        Guid WaveSubtype(int tag) => new($"{tag:x8}-0000-0010-8000-00aa00389b71");
        bool CanDecode(string extension) => extension switch
        {
            ".mp3" => Has(WaveSubtype(0x55)),
            ".flac" => Has(WaveSubtype(0xf1ac)),
            ".alac" => Has(WaveSubtype(0x6c61)),
            ".aac" or ".adts" or ".m4a" or ".m4b" or ".mp4" or ".3gp" or ".3g2" => Has(WaveSubtype(0x1610)) || Has(WaveSubtype(0x6c61)),
            ".wma" or ".asf" => Has(WaveSubtype(0x161)) || Has(WaveSubtype(0x162)) || Has(WaveSubtype(0x163)),
            ".mpa" or ".mp2" => Has(WaveSubtype(0x50)) || Has(WaveSubtype(0x55)),
            ".amr" => Has(WaveSubtype(0x7361)) || Has(WaveSubtype(0x7362)),
            ".ac3" => Has(new Guid("e06d802c-db46-11cf-b4d1-00805f6cbbea")),
            ".ec3" => Has(new Guid("a7fb87af-2d02-42fb-a4d4-05cd93843bdd")),
            ".ogg" or ".oga" or ".opus" or ".weba" or ".webm" => Has(WaveSubtype(0x704f)) || Has(new Guid("8d2fd10b-5841-4a6b-8905-588fec1aded9")),
            _ => true, // PCM containers need no decoder MFT; remaining registered formats still require successful decoding.
        };
        Extensions = CommonExtensions.Where(extension => registered.Contains(extension) && CanDecode(extension)).ToArray();
    }

    internal static ReferenceAudioFormats Discover()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows Media Foundation\ByteStreamHandlers";
        var handlers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var root = hive.OpenSubKey(keyPath);
            if (root is null) { continue; }
            foreach (string name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) { continue; }
                foreach (string clsid in key.GetValueNames().Where(value => Guid.TryParse(value, out _)))
                {
                    if (!handlers.TryGetValue(clsid, out var names)) { handlers[clsid] = names = new(StringComparer.OrdinalIgnoreCase); }
                    names.Add(name.ToLowerInvariant());
                }
            }
        }
        // Unframed raw PCM has no identifiable rate/channel/layout metadata. Supplying an L16/PCM
        // MIME hint would make arbitrary damaged/text bytes appear to be valid audio.
        string[] raw = ["audio/l8", "audio/l16", "audio/l24", "audio/l32", "audio/pcm", "audio/x-pcm", "audio/x-raw", "audio/basic"];
        var routes = handlers.Values.SelectMany(names =>
        {
            var extensions = names.Where(name => name.StartsWith('.') && name.Skip(1).All(char.IsAsciiLetterOrDigit)).Order().ToArray();
            var mimes = names.Where(name => name.StartsWith("audio/", StringComparison.Ordinal) && !raw.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
            return mimes.Length > 0 ? mimes.Select(mime => new Route(mime, extensions)) :
                extensions.Any(CommonExtensions.Contains) ? new[] { new Route(null, extensions) } : Enumerable.Empty<Route>();
        }).OrderBy(route => route.MimeType, StringComparer.Ordinal).ToArray();
        return new(routes);
    }

    internal IEnumerable<Route?> Probes(string filename) => Routes
        .OrderBy(route => route.Extensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase) ? 0 : 1)
        .Select(route => (Route?)route).Append(null);

    internal bool SupportsPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

}
