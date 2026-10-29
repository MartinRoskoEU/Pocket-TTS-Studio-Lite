using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PocketTTSStudioLite.Services;

/// <summary>Owns only this import's optional fallback WAV and lease. Never sweeps other sessions or model storage.</summary>
internal sealed class ReferenceAudioStore : IDisposable
{
    internal static string Root => Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "reference-audio");
    private readonly string _directory;
    private readonly List<FileStream> _files = [];
    private readonly FileStream _operation;
    private bool _disposed;
    internal string FallbackPath => Path.Combine(_directory, "fallback.wav");
    internal string GenerationConfigurationPath => Path.Combine(_directory, "generation.yaml");
    internal bool PreserveFiles { get; set; }
    internal bool CleanupIncomplete { get; private set; }

    internal async Task WriteGenerationConfigurationAsync(string configuration, CancellationToken cancellationToken)
    {
        Validate(GenerationConfigurationPath);
        await using (var stream = new FileStream(GenerationConfigurationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            ValidateFile(GenerationConfigurationPath, stream.SafeFileHandle);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(configuration), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        var lease = File.OpenRead(GenerationConfigurationPath);
        try { ValidateFile(GenerationConfigurationPath, lease.SafeFileHandle); _files.Add(lease); }
        catch { lease.Dispose(); throw; }
    }

    internal ReferenceAudioStore()
    {
        Validate(Root);
        Directory.CreateDirectory(Root);
        _directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Validate(_directory);
        Directory.CreateDirectory(_directory);
        string lease = Path.Combine(_directory, ".operation.lock");
        _operation = new(lease, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { ValidateFile(lease, _operation.SafeFileHandle); }
        catch { _operation.Dispose(); throw; }
    }

    internal void WriteWav(string path, float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        if (path != FallbackPath) { throw new InvalidDataException("Unexpected reference audio destination."); }
        Validate(path);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            ValidateFile(path, stream.SafeFileHandle);
            // IEEE float WAVE: no quantization before the runtime's float conditioning pipeline.
            writer.Write("RIFF"u8); writer.Write(checked(48 + samples.Length * 4)); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)3); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(checked(sampleRate * 4));
            writer.Write((short)4); writer.Write((short)32);
            writer.Write("fact"u8); writer.Write(4); writer.Write(samples.Length);
            writer.Write("data"u8); writer.Write(checked(samples.Length * 4));
            for (int offset = 0; offset < samples.Length; offset++)
            {
                if (offset % 4096 == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                writer.Write(samples[offset]);
            }
            writer.Flush(); stream.Flush(flushToDisk: true);
        }
        var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { ValidateFile(path, read.SafeFileHandle); _files.Add(read); }
        catch { read.Dispose(); throw; }
    }

    internal static void Validate(string path)
    {
        string full = Path.GetFullPath(path);
        if (full != Root && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("Reference audio must stay in application-controlled storage."); }
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                { throw new InvalidDataException("Reference audio storage cannot contain file or directory links."); }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateFile(string path, SafeFileHandle handle)
    {
        Validate(path);
        if (!GetFileInformationByHandle(handle, out var info) || info.Links != 1 || (info.Attributes & 0x410) != 0)
        { throw new IOException("Reference audio storage requires regular independent files."); }
        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity || !string.Equals(buffer.ToString().Replace(@"\\?\", ""), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Reference audio storage resolved to an unexpected path."); }
    }

    private void DeleteOwnedFile(string filename)
    {
        if (filename is not ("fallback.wav" or "generation.yaml" or ".operation.lock")) { throw new InvalidDataException("Unexpected owned audio filename."); }
        string path = Path.Combine(_directory, filename);
        Validate(path);
        if (!File.Exists(path)) { return; }
        var parents = new List<SafeFileHandle>();
        try
        {
            var directories = new Stack<string>();
            for (string? current = _directory; current is not null; current = Path.GetDirectoryName(current)) { directories.Push(current); }
            foreach (string directory in directories)
            {
                var parent = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                parents.Add(parent);
                if (parent.IsInvalid || !GetFileInformationByHandle(parent, out var info) || (info.Attributes & 0x410) != 0x10)
                { throw new IOException("A reference audio parent directory could not be safely locked."); }
            }
            using var file = CreateFile(path, 0x10080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (file.IsInvalid) { throw new IOException("A reference audio temporary file is busy.", new Win32Exception(Marshal.GetLastWin32Error())); }
            ValidateFile(path, file);
            byte delete = 1;
            if (!SetFileInformationByHandle(file, 4, ref delete, 1)) { throw new IOException("Reference audio cleanup could not complete."); }
        }
        finally { foreach (var parent in parents) { parent.Dispose(); } }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        foreach (var file in _files) { file.Dispose(); }
        _operation.Dispose();
        if (PreserveFiles) { CleanupIncomplete = true; return; }
        foreach (string name in new[] { "fallback.wav", "generation.yaml", ".operation.lock" })
        {
            try { DeleteOwnedFile(name); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { CleanupIncomplete = true; }
        }
        try { Validate(_directory); Directory.Delete(_directory, recursive: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { CleanupIncomplete = true; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint type, ref byte value, uint size);
}
