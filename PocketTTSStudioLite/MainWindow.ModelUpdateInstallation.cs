using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private async void UpdateModel_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: PocketTtsModelSetupEntry { CanUpdate: true } entry } ||
            _checkingUpdates || _modelInstalling || _modelRemoving || _checkingModels || _checkingModelUpdates || RuntimeUpdateBusy ||
            _generating || _closingAfterGeneration || _generationCleanupBlocked || _exportRunning || _pickerOpen ||
            _selectingExportModel || _closing || _closingAfterExport) return;
        _modelInstalling = _modelUpdating = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_setupCancellation.Token);
        _modelInstallCancellation = cancellation;
        RenderModelInstallationConflicts();
        RenderExportControls();
        _modelInstallTask = ConfirmAndUpdateModelAsync(entry, cancellation);
        try { await _modelInstallTask; }
        finally
        {
            _modelInstallTask = null;
            _modelInstallCancellation = null;
            _modelInstalling = _modelUpdating = false;
            if (!_closing)
            {
                ModelInstallationProgress.Visibility = Visibility.Collapsed;
                CancelModelInstallationButton.Visibility = Visibility.Collapsed;
                RenderModelInstallationConflicts();
                RenderExportControls();
            }
        }
    }

    private async Task ConfirmAndUpdateModelAsync(PocketTtsModelSetupEntry entry, CancellationTokenSource cancellation)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ShellRoot.XamlRoot, Title = "Update model?",
            Content = $"Update {entry.Name}?\n\nInstalled revision: {entry.Revision}\nAvailable revision: {entry.UpdateCommit}\n\nDownload and verify the official voice-cloning model files? Your selected model alias will be preserved. Superseded files no longer needed by any installed model will be removed after activation. Rollback is not available.",
            PrimaryButtonText = "Update", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            bool visible = true;
            ContentDialogResult confirmation;
            try
            {
                using var hide = cancellation.Token.Register(() => DispatcherQueue.TryEnqueue(() => { if (visible) dialog.Hide(); }));
                confirmation = await dialog.ShowAsync();
            }
            finally { visible = false; }
            if (confirmation != ContentDialogResult.Primary || cancellation.IsCancellationRequested || _closing) return;
            await StopGenerationSessionAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            ShowModelInstallationCard(entry.Name);
            RenderModelInstallationProgress(new($"Preparing to update {entry.Name}..."));
            await RunModelInstallationAsync(entry.Name, cancellation, entry);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_closing) return;
            ShowModelInstallationCard(entry.Name);
            RenderModelInstallationResult(entry.Name, new(false, PocketTtsModelInstallFailure.Storage,
                "The model update could not complete. Check Models and local storage; any recorded cleanup will be retried at startup."));
        }
    }
}
