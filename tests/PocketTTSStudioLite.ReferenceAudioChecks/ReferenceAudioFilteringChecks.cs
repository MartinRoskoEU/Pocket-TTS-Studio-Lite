using PocketTTSStudioLite;
using PocketTTSStudioLite.Services;
using Windows.Storage;

internal static class ReferenceAudioFilteringChecks
{
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-FilterChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        try
        {
            var formats = ReferenceAudioFormats.Discover();
            Check(formats.Extensions.Count > 0 && !formats.Extensions.Contains("*"), "The discovered filter must contain supported extensions without a wildcard.");
            foreach (string extension in formats.Extensions)
            {
                Check(formats.SupportsPath(Path.Combine(directory, "recording" + extension)), "Picker extension rejected by drop validation: " + extension);
                Check(formats.SupportsPath(Path.Combine(directory, "recording" + extension.ToUpperInvariant())), "Extension validation must ignore case: " + extension);
            }
            foreach (string? path in new[] { null, "", " ", "recording", "recording.", "recording.bin", "recording.txt", "recording.wav.exe", "profile.safetensors" })
            { Check(!formats.SupportsPath(path), "An unsupported filename passed the shared filter: " + path); }

            string supportedPath = Path.Combine(directory, "recording" + formats.Extensions[0].ToUpperInvariant());
            await File.WriteAllTextAsync(supportedPath, "Extension filtering must not inspect audio headers.");
            var supported = await StorageFile.GetFileFromPathAsync(supportedPath);
            string unsupportedPath = Path.Combine(directory, "recording.bin");
            await File.WriteAllTextAsync(unsupportedPath, "unsupported");
            var unsupported = await StorageFile.GetFileFromPathAsync(unsupportedPath);
            string folderPath = Path.Combine(directory, "folder" + formats.Extensions[0]);
            Directory.CreateDirectory(folderPath);
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);

            Check(ReferenceAudioDrop.GetSupportedFile([supported], formats)?.Path == supported.Path, "One supported local file was rejected.");
            foreach (IReadOnlyList<IStorageItem> rejected in new IReadOnlyList<IStorageItem>[]
            {
                [], [unsupported], [folder], [supported, unsupported], [supported, supported], [supported, folder],
            })
            { Check(ReferenceAudioDrop.GetSupportedFile(rejected, formats) is null, "Unsupported, empty, multiple-file, or folder contents were accepted."); }
            var emptyFormats = new ReferenceAudioFormats([]);
            Check(emptyFormats.Extensions.Count == 0 && !emptyFormats.SupportsPath(supported.Path) &&
                ReferenceAudioDrop.GetSupportedFile([supported], emptyFormats) is null, "An empty discovered filter must reject every file.");

            // A supported extension is only a preliminary filter; retain the real decoder's validation.
            Check(formats.SupportsPath("recording.wav"), "WAV support is required for the decoder fixtures on this Windows installation.");
            string wavePath = Path.Combine(directory, "valid.wav");
            using (var writer = new BinaryWriter(File.Create(wavePath)))
            {
                const int sampleCount = 2400;
                writer.Write("RIFF"u8); writer.Write(36 + sampleCount * 2); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000);
                writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(sampleCount * 2);
                for (int index = 0; index < sampleCount; index++) { writer.Write((short)(Math.Sin(index * Math.PI / 30) * 8000)); }
            }
            var service = new ReferenceAudioService(formats);
            using var retained = await service.PrepareAsync(wavePath, default);
            Check(retained.Samples.Length == 2400 && retained.Peak > 0 && retained.Waveform.Count > 0, "A supported reference did not decode normally.");
            var samples = retained.Samples.ToArray();
            Check(ReferenceAudioDrop.GetSupportedFile([unsupported], formats) is null &&
                retained.Samples.SequenceEqual(samples) && retained.SourceLease.CanRead, "Rejected content changed or released the loaded reference.");
            string corruptPath = Path.Combine(directory, "corrupt.wav");
            await File.WriteAllTextAsync(corruptPath, "not an audio stream");
            var corrupt = await StorageFile.GetFileFromPathAsync(corruptPath);
            Check(ReferenceAudioDrop.GetSupportedFile([corrupt], formats) is not null, "Extension filtering incorrectly added header validation.");
            try { using var invalid = await service.PrepareAsync(corruptPath, default); throw new Exception("The decoder accepted corrupt audio."); }
            catch (InvalidDataException) { passed++; }
            Check(retained.Samples.SequenceEqual(samples) && retained.SourceLease.CanRead, "A failed decode damaged the previous reference.");

            string mislabeledPath = Path.Combine(directory, "wave-with-mp3-extension.mp3");
            if (formats.SupportsPath(mislabeledPath))
            {
                File.Copy(wavePath, mislabeledPath);
                using var mislabeled = await service.PrepareAsync(mislabeledPath, default);
                Check(mislabeled.Samples.SequenceEqual(samples), "Decoder retries for a supported but misleading extension stopped working.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"PASS: {passed} targeted reference-audio filtering checks. Discovered extensions, native storage-item validation, and synthetic decoder fixtures; no GUI or live application data.");
    }
}
