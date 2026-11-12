using Microsoft.UI.Xaml;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private bool _checkingApplicationUpdates;
    private ApplicationUpdateResult? _applicationUpdate;

    private async Task CheckApplicationUpdatesAsync()
    {
        if (_closing || _checkingApplicationUpdates) return;
        _checkingApplicationUpdates = true;
        ApplicationUpdateStatusText.Text = "Checking for updates...";
        ApplicationUpdateStatusText.Style = (Style)ModelSetupPanel.Resources["RuntimeNeutralTextStyle"];
        ApplicationReleaseButton.Visibility = Visibility.Collapsed;
        RenderUpdateCheckState();
        try
        {
            _applicationUpdate = await new ApplicationUpdateService(_runtimeUpdateHttp).CheckAsync(_setupCancellation.Token);
            if (_closing) return;
            ApplicationUpdateStatusText.Text = _applicationUpdate.Message;
            ApplicationUpdateStatusText.Style = (Style)ModelSetupPanel.Resources[_applicationUpdate.Available
                ? "RuntimeAccentTextStyle" : _applicationUpdate.Message == "Up to date" ? "RuntimeSuccessTextStyle" : "RuntimeNeutralTextStyle"];
            ApplicationReleaseButton.Visibility = _applicationUpdate.Available ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _checkingApplicationUpdates = false;
            if (!_closing) RenderUpdateCheckState();
        }
    }

    private async void ApplicationReleaseButton_Click(object sender, RoutedEventArgs args)
    {
        if (_applicationUpdate?.ReleaseUrl is not { } url || _closing) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(url))
                ApplicationUpdateStatusText.Text = "The release page could not be opened. Retry View release.";
        }
        catch (Exception) { ApplicationUpdateStatusText.Text = "The release page could not be opened. Retry View release."; }
    }
}
