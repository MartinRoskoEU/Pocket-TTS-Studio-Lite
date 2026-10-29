using PocketTTSStudioLite.Services;
using System.Xml.Linq;

internal static class VoiceProfileExportChecks
{
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-ProfileChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        string Destination(string audio, string alias = "english_2026-10") => PocketTtsVoiceProfileFile.Destination(directory, audio, alias);
        try
        {
            string destination = Destination("reference.wav");
            Check(Path.GetFileName(destination) == "reference_english_2026-10.safetensors", "Automatic filename differs from the requested format.");
            Check(Path.GetFileName(Destination("reference.recording.flac")) == "reference.recording_english_2026-10.safetensors", "The last reference extension was not removed correctly.");
            Check(Destination(Path.Combine(directory, "reference.mp3")) == destination, "Reference directories leaked into the filename.");
            string unsafeAlias = Destination("recording.wav", "../invalid:model?");
            Check(Path.GetDirectoryName(unsafeAlias) == directory && !Path.GetFileName(unsafeAlias).Any(Path.GetInvalidFileNameChars().Contains), "An alias introduced invalid characters or escaped the export folder.");
            Check(Path.GetFileName(Destination("bad:name?.wav")) == "bad_name__english_2026-10.safetensors", "Windows filename characters were not sanitized.");
            Check(Path.GetFileName(Destination("reference.wav", "CON.test")) == "reference__CON.test.safetensors", "Reserved model device names were not sanitized.");
            foreach (string name in new[] { "CON", "NUL.tar", "aux", "COM1", "LPT9", "COM¹", "LPT²" })
            { Check(Path.GetFileName(Destination(name + ".wav")).StartsWith("_", StringComparison.Ordinal), "A reserved device reference name was not protected: " + name); }
            Check(Path.GetFileName(Destination(".wav")) == "reference_english_2026-10.safetensors", "An empty reference stem was not handled safely.");
            string longName = Path.GetFileName(Destination(new string('r', 250) + ".wav", new string('m', 128)));
            Check(longName.Length <= 255 && longName.EndsWith("_" + new string('m', 128) + ".safetensors", StringComparison.Ordinal), "Long names lost the alias/suffix or exceeded a Windows component limit.");
            string unicode = Path.GetFileName(Destination(new string('r', 113) + "😀.wav", new string('m', 128)));
            Check(!unicode.Any(character => char.IsSurrogate(character)), "Truncation split a Unicode surrogate pair.");

            string Ready(string contents)
            {
                string ready = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".safetensors");
                File.WriteAllText(ready, contents);
                return ready;
            }
            PocketTtsVoiceProfileFile.Publish(Ready("first native bytes"), destination, false, default);
            Check(File.ReadAllText(destination) == "first native bytes", "An absent destination did not export normally.");
            Check(Destination("reference.wav") == destination, "An existing destination triggered versioning.");
            string unconfirmed = Ready("unconfirmed replacement");
            try { PocketTtsVoiceProfileFile.Publish(unconfirmed, destination, false, default); throw new Exception("An existing file was replaced without confirmation."); }
            catch (IOException) { passed++; }
            Check(File.ReadAllText(destination) == "first native bytes" && File.Exists(unconfirmed), "Unconfirmed publication altered the original.");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { PocketTtsVoiceProfileFile.Publish(Ready("cancelled bytes"), destination, true, cancellation.Token); throw new Exception("A cancelled export replaced the original."); }
                catch (OperationCanceledException) { passed++; }
            }
            Check(File.ReadAllText(destination) == "first native bytes", "Cancellation changed the original profile.");
            try { PocketTtsVoiceProfileFile.Publish(Path.Combine(directory, "missing-result.safetensors"), destination, true, default); throw new Exception("A missing new export replaced the original."); }
            catch (FileNotFoundException) { passed++; }
            Check(File.ReadAllText(destination) == "first native bytes", "Failed publication lost the original.");

            using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { PocketTtsVoiceProfileFile.Publish(Ready("locked replacement"), destination, true, default); throw new Exception("Locked destination replacement unexpectedly succeeded."); }
                catch (IOException) { passed++; }
            }
            Check(File.ReadAllText(destination) == "first native bytes", "A failed replacement damaged the locked original.");
            PocketTtsVoiceProfileFile.Publish(Ready("complete successful replacement"), destination, true, default);
            Check(File.ReadAllText(destination) == "complete successful replacement", "Confirmed successful publication did not replace the original.");
            string absent = Destination("absent.wav");
            PocketTtsVoiceProfileFile.Publish(Ready("new bytes"), absent, true, default);
            Check(File.ReadAllText(absent) == "new bytes", "A destination removed after confirmation could not be published safely.");

            string raced = Destination("concurrent.wav");
            string[] sources = Enumerable.Range(0, 12).Select(index => Ready("complete export " + index)).ToArray();
            bool[] results = await Task.WhenAll(sources.Select(source => Task.Run(() =>
            {
                try { PocketTtsVoiceProfileFile.Publish(source, raced, false, default); return true; }
                catch (IOException) { return false; }
            })));
            Check(results.Count(success => success) == 1 && Enumerable.Range(0, 12).Any(index => File.ReadAllText(raced) == "complete export " + index),
                "Concurrent exports overwrote one another or published incomplete bytes.");
            Check(File.ReadAllText(destination) == "complete successful replacement", "Concurrent publication changed another profile.");

            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var document = XDocument.Load(Path.Combine(root, "PocketTTSStudioLite", "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            var cloning = Named("VoiceProfileCard");
            var preview = Named("VoicePreviewCard");
            var export = Named("ExportVoiceProfileCard");
            var input = Named("CloningTextInput");
            const string defaultText = "Adventurer, listen carefully: at dawn, three scouts vanished near the shattered watchtower. Search the ruins, retrieve the silver key, and protect the wounded prisoner before the seventh bell. But tell me—did you hear a strange whisper beneath the storm, or was it only the wind?";
            Check(cloning.Descendants().Any(element => (string?)element.Attribute("Text") == "Generate preview") && Named("ExportModelComboBox").Ancestors().Contains(cloning), "Generate preview lost its title or Model ComboBox.");
            Check((string?)input.Attribute("Text") == defaultText && (string?)input.Attribute("AcceptsReturn") == "True" && input.Attribute("IsReadOnly") is null && input.Attribute("IsEnabled") is null,
                "The editable multiline default text is not exact.");
            Check(cloning.Descendants().Any(element => element.Name.LocalName == "Button" && (string?)element.Attribute("Content") == "Generate" &&
                (string?)element.Attribute("IsEnabled") == "False" && (string?)element.Attribute("Click") == "Generate_Click"), "Generate is not initially guarded and wired to its generation handler.");
            Check(Named("SelectDestinationButton").Ancestors().Contains(export) && Named("ExportVoiceButton").Ancestors().Contains(export) &&
                (string?)Named("ExportVoiceButton").Attribute("Content") == "Export" && (string?)Named("ExportVoiceButton").Attribute("Click") == "ExportVoice_Click", "Export voice profile lost the existing folder/export handler wiring.");
            Check(!document.Descendants().Any(element => (string?)element.Attribute(x + "Name") == "ActiveModelText" ||
                (string?)element.Attribute("Text") == "Profile name" || (string?)element.Attribute("Text") == ".safetensors"), "Removed model/profile labels remain in the UI.");
            Check((string?)Named("VoiceExportNavigationItem").Attribute("Content") == "Voice Cloning" &&
                Named("VoiceExportPanel").Descendants().Any(element => (string?)element.Attribute("Text") == "Voice Cloning") &&
                Named("VoiceExportPanel").Descendants().Any(element => (string?)element.Attribute("Text") ==
                    "Clone a voice from reference audio, preview generated speech, and export a reusable voice profile."), "The page name or exact description differs from the requested copy.");
            var flow = Named("VoiceExportColumns");
            var cards = new[] { Named("ReferenceAudioCard"), cloning, preview, export };
            Check(flow.Elements().SequenceEqual(cards), "The cards are not in Reference, Generate, Preview, Export order.");
            Check(flow.Name.LocalName == "StackPanel" && (string?)flow.Attribute("Orientation") is null or "Vertical" &&
                flow.Attribute("Height") is null, "The page does not use a naturally sized single vertical flow.");
            Check((string?)flow.Attribute("HorizontalAlignment") == "Stretch" && (string?)flow.Attribute("Spacing") == "20" &&
                cards.All(element => element.Attribute("Height") is null && (string?)element.Attribute("Padding") == "20" &&
                    (string?)element.Attribute("CornerRadius") == "8"), "Full-width alignment, card spacing, or existing card style changed.");
            Check((string?)input.Attribute("Header") == "Preview text" && input.Attribute("Height") is null, "Preview text has no visible label or retains a fixed editor height.");
            Check(Named("GenerationProgressPanel").Ancestors().Contains(cloning) && !Named("GenerationProgressPanel").Ancestors().Contains(preview),
                "First-generation progress is hidden inside the initially collapsed preview card.");
            Check((string?)preview.Attribute("Visibility") == "Collapsed" && Named("VoicePreviewPlayer").Ancestors().Contains(preview) &&
                !preview.Descendants().Any(element => element == Named("SelectDestinationButton") || element == Named("ExportVoiceButton")),
                "The empty preview card is visible or still contains export controls.");
            Check((string?)Named("SelectReferenceButton").Attribute("AllowDrop") == "True" && Named("SelectReferenceButton").Attribute("Visibility") is null &&
                Named("ReferencePlayer").Ancestors().Contains(Named("ReferenceAudioCard")), "The always-available import area or shared reference player was lost.");
            Check(Named("ReferenceAudioCard").Descendants().Any(element =>
                ((string?)element.Attribute("Text"))?.Contains("ConverterParameter=ReferenceName", StringComparison.Ordinal) == true &&
                ((string?)element.Attribute("Visibility"))?.Contains("ConverterParameter=Visibility", StringComparison.Ordinal) == true),
                "The reference filename reserves empty space when no path is selected.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"PASS: {passed} targeted profile filename/publication and Voice Cloning layout checks. Temporary files, atomic replacement/collision/cancellation checks, and XAML only; no GUI or native TTS runtime.");
    }
}
