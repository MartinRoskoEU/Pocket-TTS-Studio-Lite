using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PocketTTSStudioLite.Services;

/// <summary>Local MF decoding to mono float PCM for local visualization and playback, off the UI thread.</summary>
internal static class MediaFoundationAudioDecoder
{
    internal sealed record Decoded(float[] Samples, int SampleRate);
    private const uint FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE;
    private static readonly Guid AttributesId = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    private static readonly Guid ContentType = new("fc358289-3cb6-460c-a424-b6681260375a");
    private static readonly Guid OriginName = new("fc358288-3cb6-460c-a424-b6681260375a");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71"), Float = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), Alignment = new("322de230-9eeb-43bd-ab7a-ff412251541d"), AverageBytes = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    internal static bool HasDecoder(Guid subtype)
    {
        Check(CoInitializeEx(IntPtr.Zero, 0));
        bool started = false;
        IntPtr activations = IntPtr.Zero;
        uint count = 0;
        try
        {
            Check(MFStartup(0x20070, 0)); started = true;
            var input = new TypeInfo { MajorType = Audio, Subtype = subtype };
            Check(MFTEnumEx(new Guid("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7"), 0x51, in input, IntPtr.Zero, out activations, out count));
            return count != 0;
        }
        finally
        {
            for (int index = 0; index < count; index++) { Release(Marshal.ReadIntPtr(activations, index * IntPtr.Size)); }
            if (activations != IntPtr.Zero) { Marshal.FreeCoTaskMem(activations); }
            if (started) { _ = MFShutdown(); }
            CoUninitialize();
        }
    }

    internal static Decoded Decode(string path, ReferenceAudioFormats formats, CancellationToken cancellationToken)
    {
        // No URL source is created. MIME retries route the SAME bytes to installed handlers;
        // the handler/decoder validates the content, including files with misleading extensions.
        int apartment = CoInitializeEx(IntPtr.Zero, 0);
        Check(apartment);
        bool started = false;
        try
        {
            Check(MFStartup(0x20070, 0)); started = true;
            Exception? last = null;
            foreach (var route in formats.Probes(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return DecodeStream(path, null, route, cancellationToken); }
                catch (Exception exception) when (exception is COMException or InvalidDataException) { last = exception; }
            }
            throw new InvalidDataException("Windows could not decode an audio track from this file. It may be damaged, protected, or require a codec/source handler that is not installed.", last);
        }
        finally { if (started) { _ = MFShutdown(); } CoUninitialize(); }
    }

    internal static Decoded DecodeMemory(byte[] audio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check(CoInitializeEx(IntPtr.Zero, 0));
        bool started = false;
        try
        {
            Check(MFStartup(0x20070, 0)); started = true;
            return DecodeStream(null, audio, null, cancellationToken);
        }
        finally { if (started) { _ = MFShutdown(); } CoUninitialize(); }
    }

    private static Decoded DecodeStream(string? path, byte[]? audio, ReferenceAudioFormats.Route? route, CancellationToken cancellationToken)
    {
        IStream? stream = null;
        IntPtr bytes = IntPtr.Zero, attributes = IntPtr.Zero, reader = IntPtr.Zero, type = IntPtr.Zero;
        try
        {
            if (audio is null)
            { Check(SHCreateStreamOnFileEx(path!, 0x20, 0, false, IntPtr.Zero, out stream)); } // READ, SHARE_DENY_WRITE
            else
            { stream = SHCreateMemStream(audio, checked((uint)audio.Length)) ?? throw new InvalidDataException("Windows could not open the generated audio memory stream."); }
            Check(MFCreateMFByteStreamOnStream(stream, out bytes));
            if (route is not null)
            {
                Guid iid = AttributesId;
                Check(Marshal.QueryInterface(bytes, in iid, out attributes));
                if (route.MimeType is { } mime) { Check(Method<SetString>(attributes, 25)(attributes, ContentType, mime)); }
                else { Check(Method<SetString>(attributes, 25)(attributes, OriginName, "reference" + route.Extensions[0])); }
            }
            Check(MFCreateSourceReaderFromByteStream(bytes, IntPtr.Zero, out reader));
            Check(Method<SelectStream>(reader, 4)(reader, AllStreams, false));
            Check(Method<SelectStream>(reader, 4)(reader, FirstAudio, true));
            Check(Method<GetNativeType>(reader, 5)(reader, FirstAudio, 0, out IntPtr native));
            uint nativeRate;
            try { Check(Method<GetUInt>(native, 7)(native, Rate, out nativeRate)); }
            finally { Release(native); }
            Check(MFCreateMediaType(out type));
            Check(Method<SetGuid>(type, 24)(type, MajorType, Audio));
            Check(Method<SetGuid>(type, 24)(type, Subtype, Float));
            Set(type, Channels, 1); Set(type, Rate, nativeRate); Set(type, Bits, 32); Set(type, Alignment, 4); Set(type, AverageBytes, checked(nativeRate * 4));
            Check(Method<SetType>(reader, 7)(reader, FirstAudio, IntPtr.Zero, type));
            int sampleRate = VerifyFormat(reader);
            var samples = new List<float>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
                try
                {
                    Check(Method<ReadSample>(reader, 9)(reader, FirstAudio, 0, out _, out uint flags, out _, out sample));
                    if ((flags & 1) != 0) { throw new InvalidDataException("The Windows decoder reported an audio decoding error."); }
                    if ((flags & 0x30) != 0 && VerifyFormat(reader) != sampleRate)
                    { throw new InvalidDataException("Windows could not maintain a consistent preview sample rate for this recording."); }
                    if (sample != IntPtr.Zero)
                    {
                        Check(Method<ContiguousBuffer>(sample, 41)(sample, out buffer));
                        Check(Method<LockBuffer>(buffer, 3)(buffer, out IntPtr data, out _, out uint length));
                        try
                        {
                            if (length % 4 != 0) { throw new InvalidDataException("The decoder returned incomplete PCM samples."); }
                            var chunk = new float[checked((int)(length / 4))];
                            Marshal.Copy(data, chunk, 0, chunk.Length);
                            if (chunk.Any(value => !float.IsFinite(value))) { throw new InvalidDataException("The decoded audio contains invalid PCM values."); }
                            samples.AddRange(chunk);
                        }
                        finally { Check(Method<NoArgs>(buffer, 4)(buffer)); }
                    }
                    if ((flags & 2) != 0) { break; }
                    if (sample == IntPtr.Zero && flags == 0) { throw new InvalidDataException("The decoder produced no audio data."); }
                }
                finally { Release(buffer); Release(sample); }
            }
            if (samples.Count == 0) { throw new InvalidDataException("This file contains no decodable audio samples."); }
            return new(samples.ToArray(), sampleRate);
        }
        finally
        {
            Release(type); Release(reader); Release(attributes); Release(bytes);
            if (stream is not null) { _ = Marshal.ReleaseComObject(stream); }
        }
    }

    private static int VerifyFormat(IntPtr reader)
    {
        Check(Method<GetMediaType>(reader, 6)(reader, FirstAudio, out IntPtr type));
        try
        {
            Check(Method<GetGuid>(type, 10)(type, Subtype, out Guid subtype));
            uint sampleRate = Get(type, Rate);
            if (subtype != Float || sampleRate == 0 || sampleRate > int.MaxValue || Get(type, Channels) != 1 || Get(type, Bits) != 32 || Get(type, Alignment) != 4)
            { throw new InvalidDataException("Windows could not decode this audio as PCM for preview."); }
            return (int)sampleRate;
        }
        finally { Release(type); }
    }

    // Vtable slots follow IMFAttributes, IMFSourceReader, IMFSample and IMFMediaBuffer in the Windows SDK.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
    private static void Release(IntPtr instance) { if (instance != IntPtr.Zero) { _ = Marshal.Release(instance); } }
    private static void Set(IntPtr type, Guid key, uint value) => Check(Method<SetUInt>(type, 21)(type, key, value));
    private static uint Get(IntPtr type, Guid key) { Check(Method<GetUInt>(type, 7)(type, key, out uint value)); return value; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(IntPtr self, in Guid key, in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuid(IntPtr self, in Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt(IntPtr self, in Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt(IntPtr self, in Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetString(IntPtr self, in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SelectStream(IntPtr self, uint index, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetType(IntPtr self, uint index, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetMediaType(IntPtr self, uint index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetNativeType(IntPtr self, uint stream, uint typeIndex, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSample(IntPtr self, uint index, uint flags, out uint actual, out uint resultFlags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ContiguousBuffer(IntPtr self, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(IntPtr self, out IntPtr data, out uint maximum, out uint current);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr self);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [StructLayout(LayoutKind.Sequential)] private struct TypeInfo { public Guid MajorType, Subtype; }
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, in TypeInfo input, IntPtr output, out IntPtr activations, out uint count);
    [DllImport("mfplat.dll")] private static extern int MFCreateMFByteStreamOnStream([MarshalAs(UnmanagedType.Interface)] IStream stream, out IntPtr byteStream);
    [DllImport("mfreadwrite.dll")] private static extern int MFCreateSourceReaderFromByteStream(IntPtr byteStream, IntPtr attributes, out IntPtr reader);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, [MarshalAs(UnmanagedType.Interface)] out IStream stream);
    [DllImport("shlwapi.dll")]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IStream? SHCreateMemStream(byte[] initial, uint size);
}
