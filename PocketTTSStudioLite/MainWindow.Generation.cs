using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private bool _generating, _closingAfterGeneration, _generationCleanupBlocked;
    private CancellationTokenSource? _generationCancellation;
    private Task? _generationTask;
    private Task? _generationStopTask;
    private PocketTtsGenerationSession? _generationSession;
    private AudioPlaybackData? _previewAudio;

    private void InitializeGeneration() => AppWindow.Closing += GenerationWindow_Closing;

    private bool CanGenerate => !_generationCleanupBlocked && !_generating && !_closing && !_closingAfterGeneration && !_exportRunning && !_pickerOpen &&
        !_checkingModels && !_selectingExportModel && !_referenceLoading && !_referenceBindingInvalid &&
        _referenceContextTask is null && !_modelInstalling && !_modelRemoving && !RuntimeUpdateBusy &&
        !_closingAfterReferenceAudio && !_closingAfterExport && _preparedReference is not null &&
        _modelSetup?.ActiveModel is not null && !string.IsNullOrWhiteSpace(CloningTextInput.Text);

    private void CloningText_Changed(object sender, TextChangedEventArgs args)
    { if (GenerateButton is not null) { GenerateButton.IsEnabled = CanGenerate; } }

    private async void Generate_Click(object sender, RoutedEventArgs args)
    {
        if (!CanGenerate || _preparedReference is not { } reference || _modelSetup?.ActiveModel is not { } model) { return; }
        string text = CloningTextInput.Text;
        _generating = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_setupCancellation.Token, _referenceLifetime.Token);
        _generationCancellation = cancellation;
        BeginVoiceExportNotificationOperation();
        ReferencePlayer.Pause(); VoicePreviewPlayer.Pause();
        GenerationProgressText.Text = "Preparing local speech generation…";
        RenderExportControls();
        _generationTask = GenerateCoreAsync(reference, model, text, cancellation.Token);
        try { await _generationTask; }
        finally
        {
            _generationTask = null; _generationCancellation = null; _generating = false;
            if (!_closing) { RenderExportControls(); QueueReferenceContextRefresh(); }
        }
    }

    private async Task GenerateCoreAsync(PreparedReferenceAudio reference, PocketTtsInstalledModel model, string text, CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(message =>
        {
            if (!_closing && _generating && !cancellationToken.IsCancellationRequested) { GenerationProgressText.Text = message; }
        });
        try
        {
            if (_generationSession?.Matches(reference, model) != true)
            {
                await StopGenerationSessionAsync();
                _generationSession = await PocketTtsGenerationSession.StartAsync(reference, model, progress, cancellationToken);
            }
            var audio = await _generationSession.GenerateAsync(text, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_closing || _closingAfterGeneration) { return; }
            VoicePreviewPlayer.SetAudio(audio);
            _previewAudio = audio; // The shared control releases the previous source; no historical audio is retained.
            VoicePreviewCard.Visibility = Visibility.Visible;
            try { await VoicePreviewPlayer.OpenAsync(CancellationToken.None); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Preview playback unavailable", "Speech was generated and its waveform is available, but Windows could not open playback.");
                return;
            }
            ShowVoiceExportNotification(VoiceExportNotificationKind.Success, "Voice preview ready", "Speech was generated locally. Play or seek the preview below.");
        }
        catch (OperationCanceledException)
        {
            bool stopped = await StopGenerationAfterFailureAsync();
            ShowVoiceExportNotification(stopped ? VoiceExportNotificationKind.Information : VoiceExportNotificationKind.Warning,
                stopped ? "Generation cancelled" : "Generation cleanup incomplete",
                stopped ? (_previewAudio is null ? "No preview was created." : "The previous successful preview remains available.") : "The runtime could not be confirmed stopped. Retained native-operation metadata requires review.");
        }
        catch (Exception exception)
        {
            if (exception is PocketTtsGenerationCleanupException) { _generationCleanupBlocked = true; }
            bool stopped = await StopGenerationAfterFailureAsync();
            string message = exception is InvalidDataException or ArgumentException or IOException ? exception.Message :
                "Local generation could not complete. Check the runtime, model, reference audio, text, and available memory, then retry.";
            ShowVoiceExportNotification(stopped ? VoiceExportNotificationKind.Error : VoiceExportNotificationKind.Warning,
                "Generation failed", message + (stopped && !_generationCleanupBlocked ? (_previewAudio is null ? " No preview was created." : " The previous successful preview remains available.") : " Generation cleanup could not be confirmed; retained files and native-operation metadata require review."));
        }
    }

    private void CancelGeneration_Click(object sender, RoutedEventArgs args)
    {
        GenerationProgressText.Text = "Cancelling generation and stopping the local runtime…";
        _generationCancellation?.Cancel();
    }

    private async Task StopGenerationSessionAsync()
    {
        if (_generationStopTask is { IsCompleted: false } pending) { await pending; return; }
        var session = _generationSession;
        _generationSession = null;
        if (session is null) { return; }
        var stopping = session.DisposeAsync().AsTask();
        _generationStopTask = stopping;
        try { await stopping; }
        catch (Exception) { _generationCleanupBlocked = true; throw; }
        finally { if (ReferenceEquals(_generationStopTask, stopping)) { _generationStopTask = null; } }
    }

    private async Task<bool> StopGenerationAfterFailureAsync()
    {
        try { await StopGenerationSessionAsync(); return true; }
        catch (Exception) { _generationCleanupBlocked = true; return false; }
    }

    private async void GenerationWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingAfterGeneration) { args.Cancel = true; return; }
        if (_generationTask is not { IsCompleted: false } && _generationSession is null && _generationStopTask is not { IsCompleted: false }) { return; }
        args.Cancel = true;
        _closingAfterGeneration = true;
        _generationCancellation?.Cancel();
        if (_generationTask is { } task) { await task; }
        bool stopped = await StopGenerationAfterFailureAsync();
        _generating = false; _generationTask = null;
        _closingAfterGeneration = false;
        if (!stopped)
        {
            ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Generation cleanup incomplete", "The local runtime could not be confirmed stopped. Review the retained operation metadata before retrying model operations.");
            return;
        }
        Close();
    }
}
