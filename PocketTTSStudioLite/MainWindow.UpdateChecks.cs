using Microsoft.UI.Xaml;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private bool _checkingUpdates;

    private async Task CheckUpdatesAsync()
    {
        if (_closing || _checkingUpdates || _checkingRuntimeUpdates || _checkingModelUpdates || _checkingModels ||
            _modelInstalling || _modelRemoving || RuntimeUpdateBusy || _exportRunning || _pickerOpen || _generating || _selectingExportModel) return;
        _checkingUpdates = true;
        RenderModelInstallationConflicts();
        RenderExportControls();
        try
        {
            // Both existing checkers report their own failures; neither installs anything.
            await CheckRuntimeUpdatesAsync(forceRefresh: true);
            if (!_closing) await CheckInstalledModelUpdatesAsync();
        }
        finally
        {
            _checkingUpdates = false;
            if (!_closing) { RenderModelInstallationConflicts(); RenderExportControls(); }
        }
    }

    private void RenderUpdateCheckState()
    {
        bool checking = _checkingUpdates || _checkingRuntimeUpdates || _checkingModelUpdates;
        CheckModelsButton.IsEnabled = !checking && !_checkingModels && !_modelInstalling && !_modelRemoving && !RuntimeUpdateBusy &&
            !_exportRunning && !_pickerOpen && !_generating && !_selectingExportModel && !_closing;
        UpdateChecksProgress.IsActive = checking && !_closing;
        UpdateChecksProgress.Visibility = checking && !_closing ? Visibility.Visible : Visibility.Collapsed;
    }
}
