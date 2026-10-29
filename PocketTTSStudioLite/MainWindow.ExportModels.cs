using Microsoft.UI.Xaml.Controls;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private readonly PocketTtsModelSelectionSettings _modelSelectionSettings = new();
    private bool _synchronizingExportModels, _selectingExportModel;

    private bool ExportModelSelectionBusy => _generating || _closingAfterGeneration || _checkingModels || _selectingExportModel || _modelInstalling || _modelRemoving ||
        RuntimeUpdateBusy || _exportRunning || _pickerOpen || _closingAfterExport || _closingAfterReferenceAudio || _closing;

    private void RefreshExportModelChoices()
    {
        _synchronizingExportModels = true;
        try
        {
            // A filtered view of the existing checked Settings rows, never another inventory.
            var choices = _modelSetup?.GetExportModels(_modelRows) ?? Array.Empty<PocketTtsModelSetupEntry>();
            ExportModelComboBox.ItemsSource = choices;
            ExportModelComboBox.SelectedItem = choices.FirstOrDefault(entry => ReferenceEquals(entry.Model, _modelSetup?.ActiveModel));
        }
        finally { _synchronizingExportModels = false; }
        RenderExportModelControls();
    }

    private void RenderExportModelControls()
    {
        bool available = ExportModelComboBox.Items.Count > 0;
        ExportModelComboBox.IsEnabled = available && !ExportModelSelectionBusy;
    }

    private async void ExportModel_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizingExportModels || ExportModelSelectionBusy || _modelSetup is null) { return; }
        if (ExportModelComboBox.SelectedItem is not PocketTtsModelSetupEntry entry) { return; }
        _selectingExportModel = true;
        BeginVoiceExportNotificationOperation();
        RenderExportControls();
        try
        {
            await StopGenerationSessionAsync();
            await _modelSetup.ActivateAsync(entry, _setupCancellation.Token);
            try { await _modelSelectionSettings.SaveAsync(entry.Name, _setupCancellation.Token); }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
            { ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Model selection was not saved", "The model is active, but its selection could not be saved. Check local application data permissions."); }
        }
        catch (OperationCanceledException) when (_setupCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            _modelSetup.Deactivate(entry.Name);
            _modelSetup.ClearActiveModel();
            ShowVoiceExportNotification(VoiceExportNotificationKind.Error, "Model activation failed", "The selected model could not be activated. It may have been removed or changed. Re-enter Voice Cloning to refresh models.");
        }
        finally
        {
            _selectingExportModel = false;
            if (!_closing) { RefreshExportModelChoices(); RenderExportControls(); }
        }
    }
}
