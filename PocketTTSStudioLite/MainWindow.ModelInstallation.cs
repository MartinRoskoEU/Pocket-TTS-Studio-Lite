using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketTTSStudioLite.Services;
using PocketTTSStudioLite.Services.Authentication;
using System.Net.Http;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private readonly HttpClient _modelInstallHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _modelInstallCancellation;
    private Task? _modelInstallTask;
    private bool _modelInstalling;
    private bool _closingAfterModelInstall;
    private bool _modelInstallationResultShown;

    private void InitializeModelInstallation()
    {
        AppWindow.Closing += ModelInstallationWindow_Closing;
        AppWindow.Closing += ModelRemovalWindow_Closing;
    }

    private async void InstallModel_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: PocketTtsModelSetupEntry { CanInstall: true } entry } ||
            _modelInstalling || _modelRemoving || _checkingModels || ModelUpdateBlocksChanges || RuntimeUpdateBusy || _closing || _closingAfterExport) { return; }
        _modelInstalling = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_setupCancellation.Token);
        _modelInstallCancellation = cancellation;
        ShowModelInstallationCard(entry.Name);
        RenderModelInstallationProgress(new($"Preparing to install {entry.Name}..."));
        RenderModelInstallationConflicts();
        _modelInstallTask = RunModelInstallationAsync(entry.Name, cancellation);
        try { await _modelInstallTask; }
        finally
        {
            _modelInstallTask = null;
            _modelInstallCancellation = null;
            _modelInstalling = false;
            if (!_closing)
            {
                ModelInstallationProgress.Visibility = Visibility.Collapsed;
                CancelModelInstallationButton.Visibility = Visibility.Collapsed;
                RenderModelInstallationConflicts();
            }
        }
    }

    private async Task RunModelInstallationAsync(string alias, CancellationTokenSource cancellation)
    {
        var progress = new Progress<PocketTtsModelInstallProgress>(value => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing && _modelInstalling && ReferenceEquals(_modelInstallCancellation, cancellation))
            { RenderModelInstallationProgress(value); }
        }));
        PocketTtsModelInstallResult result;
        // Every installation starts anonymously and owns any later OAuth session exclusively.
        _authentication.SignOut();
        using var clearSessionOnCancellation = cancellation.Token.Register(_authentication.SignOut);
        try
        {
            var installer = new PocketTtsModelInstallService(_modelInstallHttp, _authentication.GetAccessTokenForProcess);
            result = await installer.InstallAsync(alias, progress, cancellation.Token);
            if (result.Failure is PocketTtsModelInstallFailure.AuthenticationRequired or PocketTtsModelInstallFailure.AuthenticationFailed)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                RenderModelInstallationProgress(new("Sign in to Hugging Face to authorize the model download."));
                var dialog = new InstallationAuthenticationDialog(_authentication) { XamlRoot = ShellRoot.XamlRoot };
                var authenticationStatus = await dialog.AuthorizeAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (authenticationStatus.State == HuggingFaceAuthenticationState.SignedIn && _authentication.IsSignedIn)
                { result = await installer.InstallAsync(alias, progress, cancellation.Token); }
                else
                {
                    bool cancelled = authenticationStatus.State == HuggingFaceAuthenticationState.Cancelled;
                    result = new(false, cancelled ? PocketTtsModelInstallFailure.Cancelled : PocketTtsModelInstallFailure.AuthenticationFailed,
                        cancelled ? "Model installation cancelled during sign-in. No model was installed."
                        : "Hugging Face authentication did not complete. " + authenticationStatus.Message);
                }
            }
        }
        catch (OperationCanceledException)
        { result = new(false, PocketTtsModelInstallFailure.Cancelled, "Model installation cancelled. No incomplete installation was registered."); }
        catch (Exception)
        { result = new(false, PocketTtsModelInstallFailure.Storage, "Model installation could not complete. Check local storage permissions and retry."); }
        finally
        {
            // Clear before refreshing Settings, including every failure/cancellation path.
            // This only clears this application's session; it never signs out the browser.
            _authentication.SignOut();
        }
        if (_closing) { return; }
        RenderModelInstallationResult(alias, result);
        if (result.Succeeded && !_closingAfterModelInstall)
        {
            // Refresh verification only. Startup activation/navigation is deliberately not invoked.
            _modelInstalling = false;
            await CheckModelsAsync();
            _automaticModelUpdateCheckPending = true;
        }
    }

    private void RenderModelInstallationProgress(PocketTtsModelInstallProgress progress)
    {
        if (_modelInstallationResultShown) { return; }
        ModelInstallationStatusText.Text = progress.Message;
        ModelInstallationProgress.Visibility = Visibility.Visible;
        ModelInstallationProgress.IsIndeterminate = progress.Percent is null;
        ModelInstallationProgress.Value = progress.Percent ?? 0;
        CancelModelInstallationButton.Visibility = Visibility.Visible;
        CancelModelInstallationButton.IsEnabled = progress.CanCancel && (_modelRemovalCancellation ?? _modelInstallCancellation)?.IsCancellationRequested == false;
    }

    private void ShowModelInstallationCard(string alias)
    {
        _modelInstallationResultShown = false;
        CancelModelInstallationButton.Content = "Cancel installation";
        ModelInstallationTitleText.Text = "Installing model";
        ModelInstallationTitleText.Style = (Style)ModelInstallationPanel.Resources["InstallationTitleStyle"];
        ModelInstallationModelText.Text = alias;
        ModelInstallationModelText.Visibility = Visibility.Visible;
        ModelInstallationResultIcon.Visibility = Visibility.Collapsed;
        ModelInstallationHeading.ColumnSpacing = 0;
        ModelInstallationHeading.MinHeight = 0;
        DismissModelInstallationButton.Visibility = Visibility.Collapsed;
        ModelInstallationPanel.Visibility = Visibility.Visible;
        RevealModelInstallationCard();
    }

    private void RenderModelInstallationResult(string alias, PocketTtsModelInstallResult result)
    {
        _modelInstallationResultShown = true;
        bool cancelled = !result.Succeeded && result.Failure == PocketTtsModelInstallFailure.Cancelled;
        ModelInstallationTitleText.Text = result.Succeeded ? "Installation completed" : cancelled ? "Installation cancelled" : "Installation failed";
        string titleStyle = result.Succeeded ? "InstallationSuccessTitleStyle" : cancelled ? "InstallationNeutralTitleStyle" : "InstallationErrorTitleStyle";
        ModelInstallationTitleText.Style = (Style)ModelInstallationPanel.Resources[titleStyle];
        ModelInstallationResultIcon.Glyph = result.Succeeded ? "\uE73E" : cancelled ? "\uE711" : "\uEA39";
        ModelInstallationResultIcon.Visibility = Visibility.Visible;
        ModelInstallationHeading.ColumnSpacing = 10;
        ModelInstallationHeading.MinHeight = 32;
        ModelInstallationModelText.Visibility = result.Succeeded ? Visibility.Collapsed : Visibility.Visible;
        ModelInstallationStatusText.Text = result.Succeeded ? $"{alias} was installed and verified." : result.Message;
        ModelInstallationProgress.Visibility = Visibility.Collapsed;
        CancelModelInstallationButton.Visibility = Visibility.Collapsed;
        DismissModelInstallationButton.Visibility = Visibility.Visible;
        RevealModelInstallationCard();
    }

    private void DismissModelInstallation_Click(object sender, RoutedEventArgs args)
    {
        if (_modelInstallationResultShown) { ModelInstallationPanel.Visibility = Visibility.Collapsed; }
    }

    private void RevealModelInstallationCard() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || ModelSetupPanel.Visibility != Visibility.Visible || ModelInstallationPanel.Visibility != Visibility.Visible) { return; }
        ModelInstallationPanel.UpdateLayout();
        ModelInstallationPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
    });

    private void RenderModelInstallationConflicts()
    {
        ModelListHost.IsEnabled = !_modelInstalling && !_modelRemoving && !_checkingModels && !ModelUpdateBlocksChanges && !RuntimeUpdateBusy;
        CheckModelsButton.IsEnabled = !_modelInstalling && !_modelRemoving && !_checkingModels && !_checkingModelUpdates && !RuntimeUpdateBusy;
        RenderRuntimeUpdateStatus();
        TryStartPendingAutomaticModelUpdateCheck();
    }

    private void CancelModelInstallation_Click(object sender, RoutedEventArgs args)
    {
        _modelInstallCancellation?.Cancel();
        _modelRemovalCancellation?.Cancel();
        CancelModelInstallationButton.IsEnabled = false;
    }

    private async void ModelInstallationWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_modelInstallTask is not { IsCompleted: false } task) { return; }
        args.Cancel = true;
        if (_closingAfterModelInstall) { return; }
        _closingAfterModelInstall = true;
        _modelInstallCancellation?.Cancel();
        try { await task; }
        catch (Exception) { /* The workflow reports safe errors and awaits its own temporary-file cleanup. */ }
        _closingAfterModelInstall = false;
        Close();
    }
}
