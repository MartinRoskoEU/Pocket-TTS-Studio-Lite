using PocketTTSStudioLite;
using PocketTTSStudioLite.Services;
using System.Xml.Linq;

internal static class VoiceExportNotificationChecks
{
    internal static void Run()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        var state = new VoiceExportNotificationState();
        Check(state.Current is null, "Notifications must initially be inactive.");
        var failure = new VoiceExportNotification(VoiceExportNotificationKind.Error, "Import failed", "Invalid recording.");
        Check(state.Show(failure) && state.Current == failure, "An error was not activated.");
        Check(!state.Show(failure with { }) && state.Current == failure, "A duplicate event was shown twice.");
        state.Dismiss();
        Check(state.Current is null && !state.Show(failure with { }), "Repeated callbacks reopened a dismissed notification.");
        var warning = new VoiceExportNotification(VoiceExportNotificationKind.Warning, "Playback unavailable", "The reference remains available for export.");
        Check(state.Show(warning) && state.Current == warning, "A new event did not replace the dismissed notification.");
        Check(state.Show(failure) && state.Current == failure, "A new failure did not replace a visible warning.");
        state.BeginOperation();
        Check(state.Current is null && state.Show(failure), "An explicit new operation could not report the same failure again.");
        foreach (var kind in Enum.GetValues<VoiceExportNotificationKind>())
        {
            var notification = new VoiceExportNotification(kind, "Heading", "Message");
            Check(state.Show(notification) && state.Current == notification, "A notification kind was not supported: " + kind);
        }
        var previous = state.Current;
        Check(!state.Show(new(VoiceExportNotificationKind.Error, "Empty", " ")) && state.Current == previous,
            "An empty status cleared an unrelated notification.");
        state.Dismiss(); state.Dismiss();
        Check(state.Current is null, "Dismissal was not idempotent.");

        var successResult = new PocketTtsVoiceExportResult("voice.safetensors", 0, PocketTtsVoiceExportFailure.None, "Exported.");
        var success = VoiceExportNotification.ForExportResult(successResult, usesFallback: false);
        Check(success.Kind == VoiceExportNotificationKind.Success && success.Message.Contains(successResult.DestinationPath), "Successful export lost its type or destination.");
        foreach (var failureKind in Enum.GetValues<PocketTtsVoiceExportFailure>().Where(kind => kind != PocketTtsVoiceExportFailure.None))
        {
            var result = new PocketTtsVoiceExportResult("voice.safetensors", 1, failureKind, "Specific actionable failure.");
            var notification = VoiceExportNotification.ForExportResult(result, usesFallback: false);
            Check(notification.Kind == VoiceExportNotificationKind.Error && notification.Message == result.Message,
                "Export failure classification or its existing message changed: " + failureKind);
        }
        Check(VoiceExportNotification.ForExportResult(successResult with { ExitCode = 1 }, false).Kind == VoiceExportNotificationKind.Error,
            "A nonzero exit code was presented as success.");
        foreach (var result in new[] { successResult, successResult with { ExitCode = 1, Failure = PocketTtsVoiceExportFailure.Audio, Message = "Audio failed." } })
        {
            var plain = VoiceExportNotification.ForExportResult(result, false);
            var fallback = VoiceExportNotification.ForExportResult(result, true);
            Check(fallback.Kind == plain.Kind && fallback.Heading == plain.Heading && fallback.Message.StartsWith(plain.Message, StringComparison.Ordinal) &&
                fallback.Message.Contains("WAV fallback", StringComparison.Ordinal), "Fallback information replaced the export outcome instead of being combined with it.");
        }

        // Inspect layout contracts without loading WinUI or launching the application.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var document = XDocument.Load(Path.Combine(root, "PocketTTSStudioLite", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        var panel = Named("VoiceExportNotificationPanel");
        var page = Named("VoiceExportPanel");
        var children = page.Elements().ToArray();
        Check(panel.Parent == page && children[1] == panel && children[0].Descendants().Any(element => element.Name.LocalName == "TextBlock" && (string?)element.Attribute("Text") == "Voice Cloning") &&
            Array.IndexOf(children, Named("VoiceExportColumns")) > 1, "Notification placement is not between the page heading and audio/profile cards.");
        Check((string?)panel.Attribute("Visibility") == "Collapsed" && (string?)panel.Attribute("Style") == "{StaticResource NotificationPanelStyle}",
            "Inactive notifications must collapse and use the shared card style.");
        Check(panel.Descendants().Any(element => element.Name.LocalName == "Button" && (string?)element.Attribute("Click") == "DismissVoiceExportNotification_Click" &&
            (string?)element.Attribute("VerticalAlignment") == "Top"), "The upper-right dismiss action is missing.");
        Check(!document.Descendants().Any(element => (string?)element.Attribute(x + "Name") == "ReferenceAudioStatusText") &&
            (string?)Named("ExportStatusText").Attribute("Visibility") == "Collapsed", "Redundant inline statuses remain visible.");
        Check((string?)Named("ModelInstallationPanel").Attribute("Style") == (string?)panel.Attribute("Style"), "Settings and Voice Export do not share notification card styling.");
        Console.WriteLine($"PASS: {passed} targeted Voice Export notification checks. Lifecycle, dismissal/deduplication, result mapping, and XAML structure only; no GUI, native runtime, or live application data.");
    }
}
