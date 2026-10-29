using PocketTTSStudioLite.Services;
using Microsoft.UI.Xaml;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private bool _referenceBindingInvalid = true, _referenceRefreshPending;
    private Task? _referenceContextTask;
    private FileSystemWatcher? _referenceWatcher;
    private DispatcherTimer? _referenceContextTimer;
    private readonly CancellationTokenSource _referenceLifetime = new();

    private void InitializeReferenceBindingWatcher()
    {
        if (_referenceWatcher is not null || _closing || !Directory.Exists(PocketTtsRuntimeStore.LocalDataPath)) { return; }
        _referenceContextTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _referenceContextTimer.Tick -= ReferenceContextTimer_Tick;
        _referenceContextTimer.Tick += ReferenceContextTimer_Tick;
        try
        {
            _referenceWatcher = new FileSystemWatcher(PocketTtsRuntimeStore.LocalDataPath, "runtime-identity.json")
            { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _referenceWatcher.Changed += ReferenceMetadataChanged;
            _referenceWatcher.Created += ReferenceMetadataChanged;
            _referenceWatcher.Deleted += ReferenceMetadataChanged;
            _referenceWatcher.Renamed += (sender, args) =>
            {
                ReferenceMetadataChanged(sender, args);
                if (args.OldName == "runtime-identity.json")
                { DispatcherQueue.TryEnqueue(QueueReferenceContextRefresh); }
            };
            _referenceWatcher.Error += (_, _) => DispatcherQueue.TryEnqueue(QueueReferenceContextRefresh);
            _referenceWatcher.EnableRaisingEvents = true;
        }
        catch (IOException) { _referenceWatcher?.Dispose(); _referenceWatcher = null; }
        catch (UnauthorizedAccessException) { _referenceWatcher?.Dispose(); _referenceWatcher = null; }
    }

    private void ReferenceMetadataChanged(object sender, FileSystemEventArgs args)
    {
        if (args.Name != "runtime-identity.json") { return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || _closingAfterReferenceAudio) { return; }
            QueueReferenceContextRefresh();
        });
    }

    private async void ReferenceContextTimer_Tick(object? sender, object args)
    {
        _referenceContextTimer?.Stop();
        await RefreshReferenceBindingAsync();
    }

    private void QueueReferenceContextRefresh()
    {
        InitializeReferenceBindingWatcher();
        if (_closing || _closingAfterReferenceAudio || _referenceAudioPath is null && _preparedReference is null) { return; }
        _referenceRefreshPending = true;
        _referenceContextTimer?.Stop(); _referenceContextTimer?.Start();
    }

    private async Task InvalidateReferenceBindingAsync()
    {
        _referenceBindingInvalid = true;
        ReferencePlayer.Clear();
        bool stopped = await StopGenerationAfterFailureAsync();
        if (_preparedReference is not null)
        { SetReferenceStatus(stopped ? "The active runtime changed. Reload reference audio before preview or export." : "The runtime changed and generation cleanup could not be confirmed. Review retained native-operation metadata before restarting.", VoiceExportNotificationKind.Warning, "Reference audio needs reloading"); }
        RenderExportControls();
    }

    private static async Task<string> ReadReferenceRuntimeIdentityAsync(CancellationToken cancellationToken)
    {
        var runtime = await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(cancellationToken).ConfigureAwait(false);
        return PocketTtsCliService.ReferenceRuntimeIdentity(runtime.Identity);
    }

    private async Task RefreshReferenceBindingAsync()
    {
        if (_closing || _closingAfterReferenceAudio || _referenceLifetime.IsCancellationRequested) { return; }
        if (_referenceAudioPath is null && _preparedReference is null) { _referenceRefreshPending = false; return; }
        if (_generating || _referenceLoading || _exportRunning || _pickerOpen || RuntimeUpdateBusy || _modelRemoving || _referenceContextTask is not null)
        { _referenceRefreshPending = true; return; }
        _referenceRefreshPending = false;
        _referenceContextTask = RefreshReferenceBindingCoreAsync();
        RenderExportControls();
        try { await _referenceContextTask; }
        finally
        {
            _referenceContextTask = null;
            if (!_closing && !_closingAfterReferenceAudio)
            {
                RenderExportControls();
                if (_referenceRefreshPending) { QueueReferenceContextRefresh(); }
            }
        }
    }

    private async Task RefreshReferenceBindingCoreAsync()
    {
        try
        {
            string identity = await ReadReferenceRuntimeIdentityAsync(_referenceLifetime.Token);
            if (_preparedReference is { } reference)
            {
                if (reference.RuntimeIdentity is { } previous && previous != identity)
                { await InvalidateReferenceBindingAsync(); }
                else { reference.RuntimeIdentity = identity; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Runtime availability is an export requirement. Missing or invalid runtime
            // metadata must not discard a Windows-decoded reference or disable playback.
        }
    }
}
