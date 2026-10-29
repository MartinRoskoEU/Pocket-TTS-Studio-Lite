using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using PocketTTSStudioLite.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private Task<ReferenceAudioFormats>? _referenceFormats;
    private PreparedReferenceAudio? _preparedReference;
    private CancellationTokenSource? _referenceImportCancellation;
    private Task? _referenceImportTask;
    private bool _referenceLoading;
    private bool _closingAfterReferenceAudio, _referenceShutdownComplete;

    private bool CanImportReference => !_closing && !_closingAfterReferenceAudio && !_closingAfterExport &&
        !_generating && !_closingAfterGeneration && !_exportRunning && !_pickerOpen && !_referenceLoading;

    private void InitializeReferenceAudio()
    {
        ReferencePlayer.Status += (message, warning) => SetReferenceStatus(message,
            warning ? VoiceExportNotificationKind.Warning : VoiceExportNotificationKind.Error);
        VoicePreviewPlayer.Status += (message, warning) => ShowVoiceExportNotification(
            warning ? VoiceExportNotificationKind.Warning : VoiceExportNotificationKind.Error, "Voice preview", message);
        AppWindow.Closing += ReferenceAudioWindow_Closing;
        _referenceFormats = Task.Run(ReferenceAudioFormats.Discover);
        _ = DisplayReferenceFormatsAsync();
        InitializeReferenceBindingWatcher();
    }

    private async Task DisplayReferenceFormatsAsync()
    {
        try
        {
            var formats = await _referenceFormats!;
            if (_closing) { return; }
            ReferenceFormatsText.Text = formats.Extensions.Count == 0 ? "No supported Windows audio extensions available" : string.Join("  ", formats.Extensions);
            ToolTipService.SetToolTip(ReferenceFormatsText, ReferenceFormatsText.Text + "\nSupported browse/drop extensions from installed Windows source handlers and codecs. Successful decoding determines audio compatibility.");
        }
        catch (Exception)
        {
            if (!_closing)
            {
                ReferenceFormatsText.Text = "";
                SetReferenceStatus("Windows audio format discovery is unavailable. Check Windows media components and try again.",
                    VoiceExportNotificationKind.Error, "Audio formats unavailable");
            }
        }
    }

    private async void SelectReference_Click(object sender, RoutedEventArgs args)
    {
        if (!CanImportReference) { return; }
        BeginVoiceExportNotificationOperation();
        _pickerOpen = true;
        RenderExportControls();
        string? path = null;
        try
        {
            var formats = await _referenceFormats!;
            if (_closing || _closingAfterReferenceAudio) { return; }
            // An empty filter defaults to all files in the Windows App SDK picker.
            if (formats.Extensions.Count == 0) { throw new InvalidOperationException("No supported reference audio extensions are available."); }
            var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("Audio files", formats.Extensions.ToList());
            var selected = await picker.PickSingleFileAsync();
            if (selected is not null && formats.SupportsPath(selected.Path)) { path = selected.Path; }
        }
        catch (Exception) { SetReferenceStatus("Could not open the audio picker. Check Windows media components and try again."); }
        finally { _pickerOpen = false; if (!_closing) { RenderExportControls(); } }
        if (!_closing && path is not null) { await ImportReferenceAsync(path); }
    }

    private async void ReferenceAudio_DragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = DataPackageOperation.None;
        args.Handled = true;
        if (!CanImportReference || !args.DataView.Contains(StandardDataFormats.StorageItems)) { return; }
        var deferral = args.GetDeferral();
        try
        {
            var formats = await _referenceFormats!;
            var items = await args.DataView.GetStorageItemsAsync();
            if (CanImportReference && ReferenceAudioDrop.GetSupportedFile(items, formats) is not null)
            {
                args.AcceptedOperation = DataPackageOperation.Copy;
            }
        }
        catch (Exception) { args.AcceptedOperation = DataPackageOperation.None; }
        finally { deferral.Complete(); }
    }

    private async void ReferenceAudio_Drop(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = DataPackageOperation.None;
        args.Handled = true;
        if (!CanImportReference) { return; }
        var deferral = args.GetDeferral();
        try
        {
            if (!args.DataView.Contains(StandardDataFormats.StorageItems)) { return; }
            var items = await args.DataView.GetStorageItemsAsync();
            if (items.Count != 1 || items[0] is not StorageFile file || string.IsNullOrEmpty(file.Path))
            { SetReferenceStatus("Drop one local audio file at a time. Folders and multiple files cannot be imported.", VoiceExportNotificationKind.Warning, "Reference audio not accepted"); return; }
            var formats = await _referenceFormats!;
            if (!CanImportReference || ReferenceAudioDrop.GetSupportedFile(items, formats) is null) { return; }
            args.AcceptedOperation = DataPackageOperation.Copy;
            await ImportReferenceAsync(file.Path);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { SetReferenceStatus("This dropped file could not be opened. Try browsing to one local audio file."); }
        finally { deferral.Complete(); }
    }

    private async Task ImportReferenceAsync(string path)
    {
        if (!CanImportReference || _closing || _closingAfterReferenceAudio || _referenceLoading || _exportRunning) { return; }
        _referenceLoading = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_setupCancellation.Token, _referenceLifetime.Token);
        _referenceImportCancellation = cancellation;
        ReferencePlayer.PauseAndRewind();
        BeginVoiceExportNotificationOperation();
        RenderExportControls();
        _referenceImportTask = ImportReferenceCoreAsync(path, cancellation.Token);
        try { await _referenceImportTask; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { SetReferenceStatus("Reference loading could not finish cleaning up audio resources. Select the recording again."); }
        finally
        {
            _referenceImportTask = null;
            _referenceImportCancellation = null;
            _referenceLoading = false;
            if (!_closing) { RenderExportControls(); QueueReferenceContextRefresh(); }
        }
    }

    private async Task ImportReferenceCoreAsync(string path, CancellationToken cancellationToken)
    {
        PreparedReferenceAudio? candidate = null;
        try
        {
            var formats = await _referenceFormats!;
            candidate = await new ReferenceAudioService(formats).PrepareAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StopGenerationSessionAsync();
            var previous = _preparedReference;
            ReleaseReferencePlayback();
            _preparedReference = candidate;
            _referenceBindingInvalid = false;
            candidate = null;
            _referenceAudioPath = _preparedReference.SourcePath;
            ReferenceAudioPathText.Text = _referenceAudioPath;
            ReferencePlayer.SetAudio(_preparedReference);
            bool cleanupIncomplete = false;
            if (previous is not null)
            {
                await Task.Run(previous.Dispose);
                cleanupIncomplete = previous.CleanupIncomplete;
            }
            string status = cleanupIncomplete ? "Some previous temporary audio was retained because cleanup could not be confirmed." : "";
            SetReferenceStatus(status, VoiceExportNotificationKind.Warning, "Reference audio warning");
            try { await ReferencePlayer.OpenAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                ReleaseReferencePlayback();
                SetReferenceStatus((status.Length == 0 ? "" : status + " ") + "Playback could not be opened. The validated reference remains available for export.", VoiceExportNotificationKind.Warning, "Reference playback unavailable");
            }
        }
        catch (OperationCanceledException)
        { SetReferenceStatus("Reference loading cancelled." + (_preparedReference is null ? "" : _referenceBindingInvalid ? " Reload a reference before preview or export." : " The previous reference remains selected."), VoiceExportNotificationKind.Information, "Reference import cancelled"); }
        catch (Exception exception)
        {
            string reason = exception switch
            {
                InvalidDataException or ArgumentException => exception.Message,
                FileNotFoundException => "The audio file is no longer available. Select it again.",
                UnauthorizedAccessException => "Windows cannot read this file or create private temporary audio. Check access permissions.",
                DllNotFoundException or EntryPointNotFoundException => "Windows Media Foundation is unavailable. Windows N editions need the Media Feature Pack.",
                IOException => "Audio could not be loaded. Check that the file is accessible and private temporary storage has free space.",
                _ => "Windows could not decode or prepare this audio. Try another file or check the installed media components.",
            };
            SetReferenceStatus(reason + (_preparedReference is null ? "" : _referenceBindingInvalid ? " Reload a reference before preview or export." : " The previous reference remains selected."), VoiceExportNotificationKind.Error, "Reference audio import failed");
        }
        finally { if (candidate is not null) { await Task.Run(candidate.Dispose); } }
    }

    private void SetReferenceStatus(string message, VoiceExportNotificationKind kind = VoiceExportNotificationKind.Error,
        string heading = "Reference audio error")
    {
        ShowVoiceExportNotification(kind, heading, message);
    }

    private void ReleaseReferencePlayback() => ReferencePlayer.ReleasePlayback();
    private void RenderReferencePlaybackControls() => ReferencePlayer.SetInteractionEnabled(
        !_referenceBindingInvalid && !_referenceLoading && !_closingAfterReferenceAudio && !_closing);

    private async void ReferenceAudioWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_generating || _closingAfterGeneration) { return; }
        if (_referenceShutdownComplete || _exportOperation is { IsCompleted: false } || _closingAfterExport) { return; }
        if (_referenceImportTask is null && _referenceContextTask is null && _preparedReference is null) { return; }
        args.Cancel = true;
        if (_closingAfterReferenceAudio) { return; }
        _closingAfterReferenceAudio = true;
        _referenceLifetime.Cancel(); _referenceContextTimer?.Stop();
        _referenceImportCancellation?.Cancel();
        RenderExportControls();
        try
        {
            if (_referenceImportTask is { } task) { await task; }
            if (_referenceContextTask is { } context) { await context; }
            ReleaseReferencePlayback();
            if (_preparedReference is { } audio) { _preparedReference = null; await Task.Run(audio.Dispose); }
        }
        catch (Exception)
        {
            // Retain uncertain audio inputs instead of forcing deletion during shutdown.
            CloseReferenceAudioFallback();
        }
        finally { _referenceShutdownComplete = true; Close(); }
    }

    private void CloseReferenceAudioFallback()
    {
        ReferencePlayer.Dispose();
        _referenceLifetime.Cancel(); _referenceContextTimer?.Stop();
        _referenceWatcher?.Dispose(); _referenceWatcher = null;
        _referenceImportCancellation?.Cancel();
        ReleaseReferencePlayback();
        if (_preparedReference is { } audio)
        {
            // Forced shutdown must not delete input that an unconfirmed native process may need later.
            audio.PreserveTemporaryFiles = true;
            audio.Dispose();
            _preparedReference = null;
        }
    }
}
