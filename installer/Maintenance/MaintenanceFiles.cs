using PocketTTSStudioLite.Services;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PocketTTSStudioLite.Maintenance;

internal sealed record OwnedFile(string RelativePath, string Sha256, string Identity);
internal sealed record OwnedDirectory(string RelativePath, string Identity);

internal static class MaintenanceFiles
{
    internal const string AppId = "336B2514-DBF3-4566-9302-95EE5D2D7DD3";
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid maintenance relative path.");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        PocketTtsModelStore.ValidatePath(path, ownedRoot: root);
        return path;
    }

    internal static async Task<OwnedFile?> CaptureAsync(string root, string relative, string? expectedHash = null, bool gitBlob = false)
    {
        string path = Resolve(root, relative);
        if (!File.Exists(path)) { if (Directory.Exists(path)) throw new InvalidDataException("A planned file is a directory."); return null; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string identity = PocketTtsModelStore.FileIdentity(path, stream.SafeFileHandle, root);
        string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (expectedHash is not null)
        {
            string actual = sha256;
            if (gitBlob)
            {
                stream.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                hash.AppendData(System.Text.Encoding.ASCII.GetBytes($"blob {stream.Length}\0"));
                byte[] buffer = new byte[65536]; int count;
                while ((count = await stream.ReadAsync(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, count));
                actual = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An owned file changed. It was preserved: " + relative);
        }
        return new(relative, sha256, identity);
    }

    internal static void Validate(OwnedFile file)
    {
        if (file is null || !Regex.IsMatch(file.Sha256 ?? "", @"\A[a-fA-F0-9]{64}\z") || !Regex.IsMatch(file.Identity ?? "", @"\A[a-f0-9]{40}\z"))
            throw new InvalidDataException("Invalid maintenance file identity.");
    }

    internal static async Task<T> ReadAsync<T>(string root, string relative)
    {
        string path = Resolve(root, relative);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PocketTtsModelStore.ValidateFile(path, stream.SafeFileHandle, ownedRoot: root);
        if (stream.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid maintenance metadata size.");
        return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Invalid maintenance metadata.");
    }

    internal static async Task WriteAsync<T>(string root, string relative, T value, bool replace = true)
    {
        string path = Resolve(root, relative), temporary = path + ".tmp";
        if (Path.Exists(temporary)) throw new InvalidOperationException("Interrupted metadata publication requires review: " + temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PocketTtsModelStore.ValidatePath(path, ownedRoot: root);
        await PocketTtsRuntimeStore.WriteJsonAsync(temporary, value, default);
        File.Move(temporary, path, overwrite: replace);
    }

    internal static List<OwnedDirectory> Directories(string root, IEnumerable<OwnedFile> files)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            for (string? parent = Path.GetDirectoryName(Resolve(root, file.RelativePath)); parent is not null; parent = Path.GetDirectoryName(parent))
            { paths.Add(parent); if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) break; }
        return paths.Where(Directory.Exists).Select(path => new OwnedDirectory(Path.GetRelativePath(root, path), PocketTtsModelStore.DirectoryIdentity(path, root)))
            .OrderByDescending(directory => directory.RelativePath.Length).ToList();
    }

    internal static void Prune(string root, IEnumerable<OwnedDirectory> directories)
    {
        foreach (var directory in directories)
        {
            string path = directory.RelativePath == "." ? root : Resolve(root, directory.RelativePath);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                PocketTtsModelStore.DeleteEmptyDirectory(path, directory.Identity, root);
        }
    }
}
