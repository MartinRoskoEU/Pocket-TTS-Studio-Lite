using Microsoft.UI.Xaml;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private readonly VoiceExportNotificationState _voiceExportNotifications = new();

    private void ShowVoiceExportNotification(VoiceExportNotificationKind kind, string heading, string message) =>
        ShowVoiceExportNotification(new(kind, heading, message));

    private void ShowVoiceExportNotification(VoiceExportNotification notification)
    {
        if (_closing || !_voiceExportNotifications.Show(notification)) { return; }
        var (style, glyph) = notification.Kind switch
        {
            VoiceExportNotificationKind.Success => ("NotificationSuccessTitleStyle", "\uE73E"),
            VoiceExportNotificationKind.Error => ("NotificationErrorTitleStyle", "\uEA39"),
            VoiceExportNotificationKind.Warning => ("NotificationWarningTitleStyle", "\uE7BA"),
            _ => ("NotificationInformationTitleStyle", "\uE946"),
        };
        VoiceExportNotificationTitleText.Style = (Style)ShellRoot.Resources[style];
        VoiceExportNotificationTitleText.Text = notification.Heading;
        VoiceExportNotificationMessageText.Text = notification.Message;
        VoiceExportNotificationIcon.Glyph = glyph;
        VoiceExportNotificationPanel.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || VoiceExportPanel.Visibility != Visibility.Visible ||
                VoiceExportNotificationPanel.Visibility != Visibility.Visible || _voiceExportNotifications.Current != notification) { return; }
            VoiceExportNotificationPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
        });
    }

    private void BeginVoiceExportNotificationOperation()
    {
        _voiceExportNotifications.BeginOperation();
        VoiceExportNotificationPanel.Visibility = Visibility.Collapsed;
    }

    private void DismissVoiceExportNotification_Click(object sender, RoutedEventArgs args)
    {
        _voiceExportNotifications.Dismiss();
        VoiceExportNotificationPanel.Visibility = Visibility.Collapsed;
    }
}
