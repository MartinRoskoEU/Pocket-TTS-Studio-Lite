using PocketTTSStudioLite.Services;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

internal static class VoiceExportSettingsChecks
{
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-ExportSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        try
        {
            string path = Path.Combine(directory, "settings", "voice-export.json");
            var settings = new PocketTtsVoiceExportSettings(path);
            Check(await settings.ReadFolderAsync() is null, "First use invented an export destination.");
            string first = Directory.CreateDirectory(Path.Combine(directory, "first folder")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(directory, "second folder")).FullName;
            await settings.SaveFolderAsync(first);
            Check(await new PocketTtsVoiceExportSettings(path).ReadFolderAsync() == first, "A restarted settings instance did not restore the chosen folder.");
            await settings.SaveFolderAsync(second);
            Check(await new PocketTtsVoiceExportSettings(path).ReadFolderAsync() == second, "A changed export folder was not persisted immediately.");
            await PocketTtsVoiceExportSettings.ValidateFolderAsync(second);
            Check(!Directory.EnumerateFileSystemEntries(second).Any(), "Folder access validation retained its probe file.");
            Directory.Delete(second);
            Check(await settings.ReadFolderAsync() == second && !Directory.Exists(second), "Reading the saved preference recreated a missing destination.");
            try { await PocketTtsVoiceExportSettings.ValidateFolderAsync(second); throw new Exception("A missing folder was accepted."); }
            catch (DirectoryNotFoundException) { passed++; }
            await settings.SaveFolderAsync(first);
            Check(await settings.ReadFolderAsync() == first, "An unavailable folder preference could not be replaced.");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await settings.SaveFolderAsync(second, cancellation.Token); throw new Exception("A cancelled save succeeded."); }
                catch (OperationCanceledException) { passed++; }
                try { await PocketTtsVoiceExportSettings.ValidateFolderAsync(first, cancellation.Token); throw new Exception("Cancelled validation succeeded."); }
                catch (OperationCanceledException) { passed++; }
            }
            Check(await settings.ReadFolderAsync() == first, "A cancelled save changed the stored preference.");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await settings.SaveFolderAsync(second); throw new Exception("A locked settings file was silently replaced."); }
                catch (IOException) { passed++; }
            }
            Check(await settings.ReadFolderAsync() == first && Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Count() == 1,
                "A failed save damaged the prior preference or retained a staging file.");
            foreach (string invalid in new[] { "relative-folder", @"\\server\share", "https://example.com/folder", "" })
            {
                try { await settings.SaveFolderAsync(invalid); throw new Exception("An invalid/nonlocal destination was saved."); }
                catch (ArgumentException) { passed++; }
                Check(await settings.ReadFolderAsync() == first, "An invalid destination changed the saved preference.");
            }

            string denied = Directory.CreateDirectory(Path.Combine(directory, "denied")).FullName;
            var deniedDirectory = new DirectoryInfo(denied);
            var originalAcl = deniedDirectory.GetAccessControl();
            var acl = deniedDirectory.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
            try
            {
                deniedDirectory.SetAccessControl(acl);
                try { await PocketTtsVoiceExportSettings.ValidateFolderAsync(denied); throw new Exception("A nonwritable folder was accepted."); }
                catch (UnauthorizedAccessException) { passed++; }
                Check(!Directory.EnumerateFiles(denied).Any(), "Denied validation left an incomplete file.");
            }
            finally { deniedDirectory.SetAccessControl(originalAcl); }

            foreach (string invalid in new[] { "{", "null", "{\"SchemaVersion\":2,\"ExportFolder\":\"C:\\\\valid\"}",
                "{\"SchemaVersion\":1,\"ExportFolder\":\"relative\"}", "{\"SchemaVersion\":1}" })
            {
                await File.WriteAllTextAsync(path, invalid);
                try { await settings.ReadFolderAsync(); throw new Exception("Malformed or incompatible preferences were restored."); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException) { passed++; }
                Check(await File.ReadAllTextAsync(path) == invalid, "Reading invalid settings rewrote them.");
            }
            await settings.SaveFolderAsync(first);
            Check(await settings.ReadFolderAsync() == first, "Explicit folder selection could not recover invalid settings.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"PASS: {passed} targeted export-folder persistence/access checks. Isolated settings and folders only; no GUI or live user preferences.");
    }
}
