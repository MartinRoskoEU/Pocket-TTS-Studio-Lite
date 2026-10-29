using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

internal enum VoiceExportNotificationKind { Success, Error, Warning, Information }

internal sealed record VoiceExportNotification(VoiceExportNotificationKind Kind, string Heading, string Message)
{
    internal static VoiceExportNotification ForExportResult(PocketTtsVoiceExportResult result, bool usesFallback)
    {
        string message = result.Succeeded ? $"Voice profile exported: {result.DestinationPath}" : result.Message;
        if (usesFallback) { message += "\nThis audio variant requires the Windows-decoded WAV fallback for the active runtime."; }
        return new(result.Succeeded ? VoiceExportNotificationKind.Success : VoiceExportNotificationKind.Error,
            result.Succeeded ? "Voice profile exported" : "Voice profile export failed", message);
    }
}

internal sealed class VoiceExportNotificationState
{
    private VoiceExportNotification? _lastShown;
    internal VoiceExportNotification? Current { get; private set; }

    internal bool Show(VoiceExportNotification notification)
    {
        if (string.IsNullOrWhiteSpace(notification.Message) || notification == _lastShown) { return false; }
        Current = _lastShown = notification;
        return true;
    }

    // Remember dismissal so repeated callbacks for this event cannot reopen it.
    internal void Dismiss() => Current = null;
    internal void BeginOperation() { Current = _lastShown = null; }
}
