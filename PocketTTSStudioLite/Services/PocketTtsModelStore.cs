using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketTTSStudioLite.Services;

/// <summary>Private immutable model blobs and operation-owned staging. Never links to or deletes shared HF cache data.</summary>
internal static class PocketTtsModelStore
{
    internal static string Root => Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "models");
    internal static string Staging => Path.Combine(Root, ".staging");

    internal static FileStream TryAcquireWrite()
    {
        EnsureDirectory(Root);
        string path = Path.Combine(Root, ".operations.lock");
        ValidatePath(path);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { ValidateFile(path, stream.SafeFileHandle); return stream; }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        { throw new InvalidOperationException("A model operation is active in this or another application instance. Wait for it to finish and retry.", exception); }
    }

    internal static string BlobPath(bool sha256, string digest)
    {
        if (digest is null || !Regex.IsMatch(digest, sha256 ? @"\A[a-fA-F0-9]{64}\z" : @"\A[a-fA-F0-9]{40}\z"))
        { throw new InvalidDataException("Invalid private model content identity."); }
        return Path.Combine(Root, "blobs", sha256 ? "sha256" : "git-blob-sha1", digest.ToLowerInvariant());
    }

    internal static void ValidatePath(string path, bool localData = false, string? ownedRoot = null)
    {
        string full = Path.GetFullPath(path);
        string root = ownedRoot ?? (localData ? PocketTtsRuntimeStore.LocalDataPath : Root);
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) && !PocketTtsRuntimeStore.IsWithin(full, root))
        { throw new InvalidDataException("Model files must remain in the application's private models directory."); }
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                { throw new InvalidDataException("Private model storage cannot contain file or directory links."); }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (ownedRoot is null && string.Equals(current, PocketTtsRuntimeStore.LocalDataPath, StringComparison.OrdinalIgnoreCase)) { break; }
        }
    }

    internal static void EnsureDirectory(string path)
    {
        ValidatePath(path);
        PocketTtsRuntimeStore.EnsureDirectory(path);
        ValidatePath(path);
    }

    internal static void ValidateFile(string path, SafeFileHandle handle, bool localData = false, string? ownedRoot = null)
    {
        ValidatePath(path, localData, ownedRoot);
        if (!GetFileInformationByHandle(handle, out var information))
        { throw new IOException("Private model file identity could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error())); }
        if (information.NumberOfLinks != 1 || (information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        { throw new InvalidDataException("Private model assets must be regular independent files, not symlinks or hardlinks."); }
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) { throw new IOException("The private model file path could not be verified."); }
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) { actual = actual[4..]; }
        if (!string.Equals(actual, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("A private model path resolves to a different location."); }
    }

    internal static IDisposable PinParentDirectories(string path)
    {
        var pins = new DirectoryPins();
        try
        {
            var directories = new Stack<string>();
            for (string? current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null; current = Path.GetDirectoryName(current))
                directories.Push(current);
            foreach (string directory in directories)
            {
                var handle = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                pins.Handles.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) ||
                    (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new IOException("The runtime destination directory could not be locked.");
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class DirectoryPins : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }

    // Readers may coexist. Publication/registration writers wait asynchronously for readers, including exports.
    internal static Task<FileStream> AcquireReadAsync(CancellationToken cancellationToken) => AcquireAsync(write: false, cancellationToken);
    internal static Task<FileStream> AcquireWriteAsync(CancellationToken cancellationToken) => AcquireAsync(write: true, cancellationToken);

    private static async Task<FileStream> AcquireAsync(bool write, CancellationToken cancellationToken)
    {
        EnsureDirectory(Root);
        string path = Path.Combine(Root, ".operations.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePath(path);
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, write ? FileAccess.ReadWrite : FileAccess.Read,
                    write ? FileShare.None : FileShare.Read);
                try { ValidateFile(path, stream.SafeFileHandle); return stream; }
                catch { stream.Dispose(); throw; }
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
        }
    }

    internal static FileStream CreateStagingFile(string path)
    {
        if (!PocketTtsRuntimeStore.IsWithin(path, Staging)) { throw new InvalidDataException("Invalid private model staging path."); }
        ValidatePath(path);
        return new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.Asynchronous);
    }

    // Delete the validated file through its exclusive handle, with all parent directories pinned
    // against rename/replacement. A pathname check followed by File.Delete would leave a race.
    internal static long DeleteRegularFile(string path, bool localData = false, string? expectedIdentity = null, string? expectedSha256 = null, string? ownedRoot = null)
    {
        ValidatePath(path, localData, ownedRoot);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
        var parents = new List<SafeFileHandle>();
        try
        {
            var directories = new Stack<string>();
            for (string? current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null; current = Path.GetDirectoryName(current))
            { directories.Push(current); }
            foreach (string directory in directories)
            {
                var parent = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                parents.Add(parent);
                if (parent.IsInvalid || !GetFileInformationByHandle(parent, out var info) ||
                    (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                { throw new IOException("A private file's parent directory could not be safely locked."); }
            }
            using var handle = CreateFile(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            { throw new IOException("The private file is busy or inaccessible.", new Win32Exception(Marshal.GetLastWin32Error())); }
            ValidateFile(path, handle, localData, ownedRoot);
            if (!GetFileInformationByHandle(handle, out var information)) { throw new IOException("Cannot inspect private file size."); }
            if (expectedIdentity is not null && Identity(information) != expectedIdentity)
            { throw new InvalidDataException("The planned temporary file changed. It was preserved."); }
            using FileStream? verification = expectedSha256 is null ? null : new FileStream(handle, FileAccess.Read);
            if (verification is not null && !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(verification)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("The retired runtime file changed. It was preserved."); }
            var disposition = new FileDisposition { DeleteFile = 1 };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            { throw new IOException("Private file deletion failed.", new Win32Exception(Marshal.GetLastWin32Error())); }
            return ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        }
        finally { foreach (var parent in parents) { parent.Dispose(); } }
    }

    internal static string FileIdentity(string path, SafeFileHandle handle, string? ownedRoot = null)
    {
        ValidateFile(path, handle, localData: true, ownedRoot: ownedRoot);
        if (!GetFileInformationByHandle(handle, out var info)) { throw new IOException("Cannot inspect temporary file identity."); }
        return Identity(info);
    }

    internal static void DeleteEmptyDirectory(string path, string expectedIdentity, string? ownedRoot = null)
    {
        ValidatePath(path, localData: true, ownedRoot: ownedRoot);
        var parents = new List<SafeFileHandle>();
        try
        {
            var directories = new Stack<string>();
            for (string? current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null; current = Path.GetDirectoryName(current))
                directories.Push(current);
            foreach (string directory in directories)
            {
                var parent = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                parents.Add(parent);
                if (parent.IsInvalid || !GetFileInformationByHandle(parent, out var info) ||
                    (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new IOException("A temporary directory's parent could not be safely locked.");
            }
            using var handle = CreateFile(path, 0x10080, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information) ||
                (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                (information.Attributes & (uint)FileAttributes.Directory) == 0 || Identity(information) != expectedIdentity)
                throw new IOException("The temporary directory is busy or changed. It was preserved.");
            var disposition = new FileDisposition { DeleteFile = 1 };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                throw new IOException("The temporary directory is not empty or cannot be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { foreach (var parent in parents) parent.Dispose(); }
    }

    internal static string DirectoryIdentity(string path, string? ownedRoot = null)
    {
        ValidatePath(path, localData: true, ownedRoot: ownedRoot);
        using var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
        { throw new IOException("The temporary storage directory could not be verified."); }
        return Identity(info);
    }

    private static string Identity(FileInformation info) => $"{info.VolumeSerialNumber:x8}{info.FileIndexHigh:x8}{info.FileIndexLow:x8}{info.CreationTime.dwHighDateTime:x8}{info.CreationTime.dwLowDateTime:x8}";

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint infoClass, ref FileDisposition info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
