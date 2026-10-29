using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private bool _modelRemoving, _closingAfterModelRemoval;
    private CancellationTokenSource? _modelRemovalCancellation;
    private Task? _modelRemovalTask;

    private void ManageModel_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: PocketTtsModelSetupEntry { CanRemove: true } }) { RemoveModel_Click(sender, args); }
        else { InstallModel_Click(sender, args); }
    }

    private async void RemoveModel_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: PocketTtsModelSetupEntry { CanRemove: true } entry } ||
            _closing || _modelRemoving || _modelInstalling || _checkingModels || _checkingModelUpdates ||
            RuntimeUpdateBusy || _exportRunning || _pickerOpen || _closingAfterExport) { return; }
        _modelRemoving = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_setupCancellation.Token);
        _modelRemovalCancellation = cancellation;
        RenderModelInstallationConflicts();
        RenderExportControls();
        _modelRemovalTask = ConfirmAndRemoveModelAsync(entry, cancellation.Token);
        try { await _modelRemovalTask; }
        finally
        {
            _modelRemovalTask = null;
            _modelRemovalCancellation = null;
            _modelRemoving = false;
            if (!_closing) { RenderModelInstallationConflicts(); RenderExportControls(); }
        }
    }

    private async Task ConfirmAndRemoveModelAsync(PocketTtsModelSetupEntry entry, CancellationToken cancellationToken)
    {
        PocketTtsModelRemovalResult result;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = ShellRoot.XamlRoot,
                Title = "Remove model?",
                Content = $"Remove {entry.Name}?\n\nIts registration and private files no longer used by other installed models will be removed permanently. Shared files and exported voice profiles will be kept. An active model will be deactivated.",
                PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            };
            using var cancelDialog = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            var confirmation = await dialog.ShowAsync();
            if (confirmation != ContentDialogResult.Primary || cancellationToken.IsCancellationRequested)
            { result = new(false, true, 0, 0, false, $"Removal of {entry.Name} cancelled. No model files or registration were changed."); }
            else
            {
                ShowModelInstallationCard(entry.Name);
                ModelInstallationTitleText.Text = "Removing model";
                CancelModelInstallationButton.Content = "Cancel removal";
                RenderModelInstallationProgress(new($"Checking model operations before removing {entry.Name}..."));
                var progress = new Progress<PocketTtsModelInstallProgress>(value =>
                {
                    if (!_closing && _modelRemoving) { RenderModelInstallationProgress(value); }
                });
                result = await new PocketTtsModelRemovalService().RemoveAsync(entry.Name, entry.InventoryIdentity!, progress, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        { result = new(false, true, 0, 0, false, "Model removal cancelled before registration changed."); }
        catch (Exception)
        { result = new(false, false, 0, 0, true, "Model removal could not complete. Refresh Models to check the registration; any committed cleanup remains saved for recovery."); }
        string? selectionSaveError = null;
        if (result.Removed)
        {
            string? activeAlias = _modelSetup?.ActiveModel?.Name;
            _modelSetup?.Deactivate(entry.Name);
            _modelUpdateResults.Remove(entry.Name);
            SetModelRows(_modelRows.Where(row => row.Name != entry.Name).ToArray());
            try { await _modelSelectionSettings.ForgetRemovedModelAsync(entry.Name, activeAlias, CancellationToken.None); }
            catch (Exception exception) when (exception is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { selectionSaveError = "The model was removed, but its saved selection could not be cleared. Check local application data permissions before restarting."; }
        }
        if (_closing) { return; }
        if (ModelInstallationPanel.Visibility != Visibility.Visible || _modelInstallationResultShown)
        { ShowModelInstallationCard(entry.Name); }
        RenderModelRemovalResult(entry.Name, result);
        RenderExportControls();
        if (!_closingAfterModelRemoval)
        {
            _modelRemoving = false;
            await CheckModelsAsync(); // Verification only; no activation or navigation.
        }
        if (selectionSaveError is not null && !_closing) { ModelInstallationStatusText.Text += "\n" + selectionSaveError; }
    }

    private void RenderModelRemovalResult(string alias, PocketTtsModelRemovalResult result, bool recovery = false)
    {
        bool success = (result.Removed || recovery) && !result.CleanupPending;
        RenderModelInstallationResult(alias, new(success, result.Cancelled ? PocketTtsModelInstallFailure.Cancelled : PocketTtsModelInstallFailure.Storage, result.Message));
        ModelInstallationTitleText.Text = result.CleanupPending ? "Model cleanup incomplete" : result.Cancelled ? "Removal cancelled" :
            success ? (recovery ? "Model cleanup completed" : "Model removed") : "Removal failed";
        ModelInstallationStatusText.Text = result.Message;
        if (result.CleanupPending)
        {
            ModelInstallationTitleText.Style = (Style)ModelInstallationPanel.Resources["InstallationNeutralTitleStyle"];
            ModelInstallationResultIcon.Glyph = "\uE7BA";
        }
    }

    private async void ModelRemovalWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_modelRemovalTask is not { IsCompleted: false } task) { return; }
        args.Cancel = true;
        if (_closingAfterModelRemoval) { return; }
        _closingAfterModelRemoval = true;
        _modelRemovalCancellation?.Cancel();
        try { await task; }
        catch (Exception) { /* A committed cleanup journal is recovered at next startup. */ }
        _closingAfterModelRemoval = false;
        Close();
    }
}
