using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private PocketTtsModelSetupService? _modelSetup;
    private readonly CancellationTokenSource _setupCancellation = new();
    private bool _checkingModels;

    private void InitializeModelSetup()
    {
        _modelSetup = new PocketTtsModelSetupService(_voiceCli);
        _ = CheckModelsAsync(isStartup: true);
    }

    private async void CheckModels_Click(object sender, RoutedEventArgs args) => await CheckUpdatesAsync();

    private async Task CheckModelsAsync(bool isStartup = false)
    {
        if (_checkingModels || _selectingExportModel || _exportRunning || _modelInstalling || _modelRemoving || _closing || RuntimeUpdateBusy || _modelSetup is null) { return; }
        _checkingModels = true;
        RenderExportControls();
        CheckModelsButton.IsEnabled = false;
        ModelListHost.IsEnabled = false;
        RenderRuntimeReadiness(null);
        RuntimeVersionText.Text = "Checking installed release…";
        ModelListEmptyText.Text = "Refreshing models…";
        ModelSetupStatusText.Text = "Checking runtime and verified private model assets…";
        ModelSetupStatusText.Visibility = Visibility.Visible;
        try
        {
            if (isStartup)
            {
                var downloads = await PocketTtsDownloadOperation.RecoverAsync(_setupCancellation.Token);
                var cleanup = await new PocketTtsModelRemovalService().RecoverAsync(_setupCancellation.Token);
                if (downloads.HadWork)
                {
                    cleanup = new PocketTtsModelRemovalResult(false, false, downloads.DeletedBytes + (cleanup?.DeletedBytes ?? 0),
                        cleanup?.SharedFilesRetained ?? 0, downloads.CleanupPending || cleanup?.CleanupPending == true,
                        downloads.Message + (cleanup is null ? "" : "\n" + cleanup.Message));
                }
                if (cleanup is not null && !_closing)
                {
                    ShowModelInstallationCard("Pending storage cleanup");
                    RenderModelRemovalResult("Pending storage cleanup", cleanup, recovery: true);
                    ModelInstallationTitleText.Text = cleanup.CleanupPending ? "Storage cleanup incomplete" : "Storage cleanup completed";
                }
            }
            var result = await _modelSetup.CheckAsync(_setupCancellation.Token);
            if (_closing) { return; }
            ModelSetupStatusText.Text = result.InventoryError is null ? result.Message : result.Message + "\n" + result.InventoryError;
            ModelSetupStatusText.Visibility = result.RuntimeReady && result.InventoryError is null ? Visibility.Collapsed : Visibility.Visible;
            SetModelRows(result.Models);
            ModelList.Visibility = result.Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelListHost.Visibility = ModelList.Visibility;
            RenderRuntimeReadiness(result.RuntimeReady);
            ModelListEmptyText.Text = result.RuntimeReady
                ? "No supported or identifiable installed models were found."
                : "Runtime support is unavailable. No identifiable installed models were found.";
            ModelListEmptyText.Visibility = result.Models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await RefreshInstalledRuntimeReleaseAsync();
            if (isStartup && !_closing)
            {
                PocketTtsModelSetupEntry? startupModel = null;
                try
                {
                    startupModel = await _modelSelectionSettings.GetStartupModelAsync(
                        _modelSetup.GetExportModels(result.Models), _setupCancellation.Token);
                }
                catch (Exception exception) when (exception is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Saved model selection unavailable", "The saved model selection could not be read. Choose a model explicitly in Voice Cloning to save a new preference."); }
                if (startupModel is null) { NavigateTo(ModelsNavigationItem); }
                else { await ActivateModelAsync(startupModel); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_closing)
            {
                if (isStartup) { NavigateTo(ModelsNavigationItem); }
                ModelSetupStatusText.Text = "Model setup could not complete. Check the runtime files and private storage permissions, then retry.";
                ModelSetupStatusText.Visibility = Visibility.Visible;
                RenderRuntimeReadiness(false);
                await RefreshInstalledRuntimeReleaseAsync();
                _modelSetup.ApplyCheckedModels(Array.Empty<PocketTtsModelSetupEntry>());
                SetModelRows(Array.Empty<PocketTtsModelSetupEntry>());
                ModelList.Visibility = Visibility.Collapsed;
                ModelListHost.Visibility = Visibility.Collapsed;
                ModelListEmptyText.Text = "Models could not be checked. Refresh after resolving the reported error.";
                ModelListEmptyText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _checkingModels = false;
            QueueReferenceContextRefresh();
            if (!_closing)
            {
                RenderModelInstallationConflicts();
                RenderExportControls();
                if (isStartup)
                {
                    StartRuntimeUpdateCheck();
                    QueueAutomaticModelUpdateCheck();
                }
            }
        }
    }

    private async Task ActivateModelAsync(PocketTtsModelSetupEntry entry)
    {
        await _modelSetup!.ActivateAsync(entry, _setupCancellation.Token);
        RefreshExportModelChoices();
        NavigateTo(VoiceExportNavigationItem);
        RenderExportControls();
    }

}

/// <summary>Presentation labels for the setup service's existing installation states.</summary>
public sealed class ModelInstallationStatusConverter : IValueConverter
{
    public static string StatusText(PocketTtsModelInstallationState state) => state switch
    {
        PocketTtsModelInstallationState.NotInstalled => "Not installed",
        PocketTtsModelInstallationState.InstalledVerified => "Installed",
        PocketTtsModelInstallationState.Incompatible => "Incompatible",
        _ => "Unverified"
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is PocketTtsModelInstallationState state ? StatusText(state) : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
