using System.Text.RegularExpressions;

namespace PocketTTSStudioLite.Services;

internal static class PocketTtsVoiceProfileFile
{
    private const string Extension = ".safetensors";

    internal static string Destination(string folder, string referenceAudioPath, string modelAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        string reference = SafeComponent(Path.GetFileNameWithoutExtension(referenceAudioPath), "reference");
        string alias = Shorten(SafeComponent(modelAlias, "model"), 128);
        // Reserve the complete alias, separator, and suffix within a 255-character Windows component.
        reference = Shorten(reference, 255 - Extension.Length - alias.Length - 1);
        return Path.Combine(Path.GetFullPath(folder), $"{reference}_{alias}{Extension}");
    }

    private static string SafeComponent(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => character < 32 || invalid.Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim(' ', '.');
        if (safe.Length == 0) { return fallback; }
        if (Regex.IsMatch(safe, @"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        { safe = "_" + safe; }
        return safe;
    }

    private static string Shorten(string value, int length)
    {
        if (value.Length <= length) { return value; }
        if (char.IsHighSurrogate(value[length - 1])) { length--; }
        return value[..length].TrimEnd(' ', '.');
    }

    internal static void Publish(string completedPath, string destination, bool overwriteConfirmed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (overwriteConfirmed && File.Exists(destination))
        { File.Replace(completedPath, destination, destinationBackupFileName: null); }
        else
        { File.Move(completedPath, destination, overwrite: false); }
    }
}
