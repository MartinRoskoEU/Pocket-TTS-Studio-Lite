using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private readonly PocketTtsCliService _voiceCli = new();
    private readonly PocketTtsVoiceExportSettings _voiceExportSettings = new();
    private string? _referenceAudioPath;
    private string? _exportFolder;
    private CancellationTokenSource? _exportCancellation;
    private Task<PocketTtsVoiceExportResult>? _exportOperation;
    private bool _exportRunning;
    private bool _pickerOpen;
    private bool _closingAfterExport;
    private bool _exportFolderLoading, _exportFolderAvailable;

    private bool VoiceExportBusy => _generationCleanupBlocked || _generating || _closingAfterGeneration || _checkingModels ||
        _selectingExportModel || _referenceContextTask is not null || _referenceLoading || _closingAfterReferenceAudio ||
        _modelInstalling || _modelRemoving || _exportRunning || _pickerOpen || _closingAfterExport || _closing || RuntimeUpdateBusy;
    private bool CanSelectExportFolder => !VoiceExportBusy && !_exportFolderLoading;
    private bool CanExportVoice => CanSelectExportFolder && !_referenceBindingInvalid && _preparedReference is not null &&
        _modelSetup?.ActiveModel is not null && _exportFolder is not null && _exportFolderAvailable;

    private void InitializeVoiceExport()
    {
        AppWindow.Closing += ExportWindow_Closing;
        InitializeGeneration();
        InitializeReferenceAudio();
        RenderExportControls();
        _ = RestoreExportFolderAsync();
    }

    private async Task RestoreExportFolderAsync()
    {
        _exportFolderLoading = true;
        RenderExportControls();
        try
        {
            string? folder = await _voiceExportSettings.ReadFolderAsync(_setupCancellation.Token);
            if (_closing || folder is null) { return; }
            _exportFolder = folder;
            ExportDestinationText.Text = folder;
            await PocketTtsVoiceExportSettings.ValidateFolderAsync(folder, _setupCancellation.Token);
            if (!_closing) { _exportFolderAvailable = true; }
        }
        catch (OperationCanceledException) when (_setupCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closing)
            {
                ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Export folder unavailable",
                    "The saved export folder could not be restored or is no longer writable. Choose an accessible local folder.");
            }
        }
        finally
        {
            _exportFolderLoading = false;
            if (!_closing) { RenderExportControls(); }
        }
    }

    private async void SelectDestination_Click(object sender, RoutedEventArgs args)
    {
        if (!CanSelectExportFolder) { return; }
        BeginVoiceExportNotificationOperation();
        _pickerOpen = true;
        RenderExportControls();
        try
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            var selected = await picker.PickSingleFolderAsync();
            if (!_closing && selected is not null)
            {
                await PocketTtsVoiceExportSettings.ValidateFolderAsync(selected.Path, _setupCancellation.Token);
                if (_closing) { return; }
                _exportFolder = selected.Path;
                _exportFolderAvailable = true;
                ExportDestinationText.Text = selected.Path;
                try { await _voiceExportSettings.SaveFolderAsync(selected.Path, _setupCancellation.Token); }
                catch (OperationCanceledException) when (_setupCancellation.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    if (!_closing)
                    {
                        ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Export folder was not saved",
                            "The folder is selected for this session, but its preference could not be saved. Check local application data permissions and select it again.");
                    }
                    return;
                }
                if (_closing) { return; }
                ShowVoiceExportNotification(VoiceExportNotificationKind.Information, "Export folder selected", "The profile filename is generated from the reference audio filename and selected model.");
            }
        }
        catch (OperationCanceledException) when (_setupCancellation.IsCancellationRequested) { }
        catch (Exception) { if (!_closing) { ShowVoiceExportNotification(VoiceExportNotificationKind.Error, "Export folder unavailable", "Could not open the folder picker or use the chosen folder. Choose an accessible, writable local export folder again."); } }
        finally { _pickerOpen = false; if (!_closing) { RenderExportControls(); } }
    }

    private async void ExportVoice_Click(object sender, RoutedEventArgs args)
    {
        if (!CanExportVoice || _preparedReference is not { } reference || _exportFolder is not { } exportFolder || _modelSetup?.ActiveModel is not { } model) { return; }
        _exportRunning = true;
        BeginVoiceExportNotificationOperation();
        ExportStatusText.Text = "";
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        RenderExportControls();
        var progress = new Progress<PocketTtsVoiceExportProgress>(update => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing && _exportRunning && ReferenceEquals(_exportCancellation, cancellation) && !cancellation.IsCancellationRequested)
            { ExportStatusText.Text = update.Message; ExportStatusText.Visibility = Visibility.Visible; }
        }));
        try
        {
            // Availability may have changed since startup or folder selection. Keep I/O off the UI thread.
            try { await PocketTtsVoiceExportSettings.ValidateFolderAsync(exportFolder, cancellation.Token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { _exportFolderAvailable = false; throw; }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing) { return; }
            string destination = PocketTtsVoiceProfileFile.Destination(exportFolder, reference.SourcePath, model.Name);
            bool overwriteConfirmed = false;
            if (File.Exists(destination))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = ShellRoot.XamlRoot,
                    Title = "Overwrite voice profile?",
                    Content = $"{Path.GetFileName(destination)} already exists. Overwrite it?\n\nThe existing file will be replaced only after the new export completes successfully.",
                    PrimaryButtonText = "Overwrite", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                };
                using var cancelDialog = cancellation.Token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
                var confirmation = await dialog.ShowAsync();
                cancellation.Token.ThrowIfCancellationRequested();
                if (_closing || _closingAfterReferenceAudio) { return; }
                if (confirmation != ContentDialogResult.Primary)
                {
                    ShowVoiceExportNotification(VoiceExportNotificationKind.Information, "Export cancelled", "The existing file was not changed.");
                    return;
                }
                overwriteConfirmed = true;
            }
            ReferencePlayer.Pause();
            _exportOperation = _voiceCli.ExportPreparedReferenceAsync(reference, destination,
                model, progress, cancellation.Token, overwriteConfirmed);
            PocketTtsVoiceExportResult result = await _exportOperation;
            if (!_closing)
            {
                ShowVoiceExportNotification(VoiceExportNotification.ForExportResult(result, reference.RuntimeNeedsFallback));
                // Model setup owns installation/access concerns; local exports do not prompt for sign-in.
            }
        }
        catch (OperationCanceledException) { if (!_closing) { ShowVoiceExportNotification(VoiceExportNotificationKind.Information, "Export cancelled", "No destination file was published."); } }
        catch (PlatformNotSupportedException exception) { ShowExportError(exception.Message); }
        catch (FileNotFoundException exception) { ShowExportError(exception.Message); }
        catch (ArgumentException exception) { ShowExportError(exception.Message); }
        catch (UnauthorizedAccessException) { ShowExportError("Cannot read the recording or write to the destination. Choose accessible local files/folders."); }
        catch (IOException exception) { ShowExportError(exception.Message); }
        catch (System.IO.InvalidDataException exception)
        {
            if (!_closing) { ShowExportError(exception.Message); _referenceRefreshPending = true; }
        }
        catch (InvalidOperationException)
        {
            reference.PreserveTemporaryFiles = true;
            ShowExportError("Export could not complete or cleanup was incomplete. Check the runtime location and the destination folder; a child process or temporary profile may remain.");
        }
        catch (Exception) { ShowExportError("Export could not complete. Check the runtime, audio, and destination, then retry."); }
        finally
        {
            _exportOperation = null;
            _exportCancellation = null;
            _exportRunning = false;
            if (_referenceRefreshPending && !_closing) { await RefreshReferenceBindingAsync(); }
            if (!_closing) { RenderExportControls(); }
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs args) => CancelActiveExport();

    private void ShowExportError(string message) =>
        ShowVoiceExportNotification(VoiceExportNotificationKind.Error, "Voice profile export failed", message);

    private void CancelActiveExport()
    {
        if (!_exportRunning || _exportCancellation is null) { return; }
        ExportStatusText.Text = "Cancelling export and waiting for the runtime to stop…";
        ExportStatusText.Visibility = Visibility.Visible;
        _exportCancellation.Cancel();
    }

    private void RenderExportControls()
    {
        RenderExportModelControls();
        SelectReferenceButton.IsEnabled = CanImportReference;
        SelectDestinationButton.IsEnabled = CanSelectExportFolder;
        ExportVoiceButton.IsEnabled = CanExportVoice;
        RenderReferencePlaybackControls();
        GenerateButton.IsEnabled = CanGenerate;
        CloningTextInput.IsEnabled = !_generating && !_closingAfterGeneration;
        GenerationProgressPanel.Visibility = _generating ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.Visibility = _exportRunning ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.IsActive = _exportRunning;
        ExportProgress.Visibility = _exportRunning ? Visibility.Visible : Visibility.Collapsed;
        ExportOperationStatusPanel.Visibility = _exportRunning ? Visibility.Visible : Visibility.Collapsed;
        ExportStatusText.Visibility = _exportRunning && !string.IsNullOrWhiteSpace(ExportStatusText.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderShellNavigation();
        RenderRuntimeUpdateStatus();
    }

    private async void ExportWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exportOperation is not { IsCompleted: false } operation) { return; }
        args.Cancel = true;
        if (_closingAfterExport) { return; }
        _closingAfterExport = true;
        CancelActiveExport();
        RenderExportControls();
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException)
        {
            _closingAfterExport = false;
            ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Export cleanup incomplete", "Cleanup could not be confirmed. The window remains open so you can check the runtime and temporary output before closing again.");
            RenderExportControls();
            return;
        }
        catch (Exception) { /* No further operation remains; the export handler reports its safe error. */ }
        _closingAfterExport = false;
        Close();
    }
}
