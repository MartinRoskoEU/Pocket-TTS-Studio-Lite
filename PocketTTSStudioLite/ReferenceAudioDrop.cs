using PocketTTSStudioLite.Services;
using Windows.Storage;

namespace PocketTTSStudioLite;

internal static class ReferenceAudioDrop
{
    internal static StorageFile? GetSupportedFile(IReadOnlyList<IStorageItem> items, ReferenceAudioFormats formats) =>
        items.Count == 1 && items[0] is StorageFile file && formats.SupportsPath(file.Path) ? file : null;
}
