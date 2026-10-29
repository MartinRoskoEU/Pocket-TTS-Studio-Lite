using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using PocketTTSStudioLite.Services;
using Windows.Foundation;
using Windows.Media.Playback;

namespace PocketTTSStudioLite;

public sealed partial class AudioPlayerControl : UserControl, IDisposable
{
    private AudioPlaybackData? _preparedReference;
    private bool _referenceOpened, _referenceEnded, _referenceLoading, _closing;
    private uint? _referenceSeekPointerId;
    private double _referencePositionSeconds;
    private MediaPlayer? _referencePlayer;
    private ReferenceAudioPlaybackSource? _referencePlaybackSource;
    private bool _referenceRendering, _referencePlaying, _referenceSeeking;
    private double? _referencePendingSeekSeconds;
    private AudioPlaybackData? _referenceWaveformOwner;
    private XamlRoot? _referenceWaveformRoot;
    private double _referenceWaveformWidth, _referenceWaveformHeight, _referenceWaveformScale;
    private readonly TranslateTransform _referencePlayheadTransform = new();
    private readonly RectangleGeometry _referencePlayedClip = new();
    public string AudioLabel { get; set; } = "reference audio";
    internal event Action<string, bool>? Status;

    public AudioPlayerControl()
    {
        InitializeComponent();
        ReferencePlayedWaveform.Clip = _referencePlayedClip;
        ReferencePlayhead.RenderTransform = _referencePlayheadTransform;
        ReferenceWaveformSurface.Loaded += ReferenceWaveform_Loaded;
        Unloaded += (_, _) =>
        {
            Pause(); EndReferenceSeeking(); _referencePendingSeekSeconds = null;
            CompositionTarget.Rendering -= ReferencePlayback_Rendering; _referenceRendering = false;
        };
    }

    internal void SetAudio(AudioPlaybackData audio)
    {
        ReleasePlayback();
        _preparedReference = audio;
        _referenceEnded = false;
        _referencePositionSeconds = 0;
        ReferenceCurrentTime.Text = FormatReferenceTime(TimeSpan.Zero);
        ReferenceTotalTime.Text = FormatReferenceTime(audio.Duration);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReferenceWaveformInput, $"{AudioLabel} waveform");
        ToolTipService.SetToolTip(ReferenceWaveformInput, $"Click or drag to seek {AudioLabel}");
        Visibility = Visibility.Visible;
        DrawReferenceWaveform();
    }

    internal void SetInteractionEnabled(bool enabled)
    {
        _referenceLoading = !enabled;
        RenderReferencePlaybackControls();
    }
    internal void Pause() { try { _referencePlayer?.Pause(); } catch (Exception) { ReleasePlayback(); } }
    internal void PauseAndRewind()
    {
        try
        {
            _referencePlayer?.Pause();
            if (_referenceOpened && _referencePlayer is not null) { _referencePlayer.PlaybackSession.Position = TimeSpan.Zero; }
            _referenceEnded = false;
            UpdateReferencePosition();
        }
        catch (Exception) { ReleasePlayback(); }
    }
    internal void Clear()
    {
        ReleasePlayback(); _preparedReference = null; _referenceWaveformOwner = null;
        ReferenceWaveformPath.Data = ReferencePlayedWaveform.Data = null;
        Visibility = Visibility.Collapsed;
        ReferencePlayhead.Visibility = Visibility.Collapsed;
        ReferenceCurrentTime.Text = ReferenceTotalTime.Text = "--:--";
        RenderReferencePlaybackControls();
    }
    private void Notify(string message, bool warning = false) => Status?.Invoke(message, warning);
    public void Dispose()
    {
        if (_closing) { return; }
        _closing = true; Clear();
        ReferenceWaveformSurface.Loaded -= ReferenceWaveform_Loaded;
        if (_referenceWaveformRoot is not null) { _referenceWaveformRoot.Changed -= ReferenceWaveformRoot_Changed; _referenceWaveformRoot = null; }
    }
    internal Task OpenAsync(CancellationToken cancellationToken)
    {
        var audio = _preparedReference ?? throw new InvalidOperationException("No audio is loaded.");
        cancellationToken.ThrowIfCancellationRequested();
        _referencePlaybackSource = new ReferenceAudioPlaybackSource(audio.Samples, audio.SampleRate);
        var player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
        _referencePlayer = player;
        player.CommandManager.IsEnabled = false;
        player.MediaOpened += ReferencePlayer_MediaOpened;
        player.MediaEnded += ReferencePlayer_MediaEnded;
        player.MediaFailed += ReferencePlayer_MediaFailed;
        player.PlaybackSession.PlaybackStateChanged += ReferenceSession_PlaybackStateChanged;
        player.PlaybackSession.SeekCompleted += ReferenceSession_SeekCompleted;
        player.Source = _referencePlaybackSource.Source;
        return Task.CompletedTask;
    }

    private void ReferencePlayer_MediaOpened(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || !ReferenceEquals(sender, _referencePlayer)) { return; }
        _referenceOpened = true;
        RenderReferencePlaybackControls();
        UpdateReferencePosition();
    });

    private void ReferencePlayer_MediaEnded(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || !ReferenceEquals(sender, _referencePlayer)) { return; }
        _referenceEnded = true;
        _referenceSeeking = false;
        _referencePlaying = false;
        UpdateReferenceRendering();
        UpdateReferencePosition();
        RenderReferencePlaybackControls();
    });

    private void ReferencePlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || !ReferenceEquals(sender, _referencePlayer)) { return; }
        ReleasePlayback();
        RenderReferencePlaybackControls();
        Notify("Playback failed. Check the Windows audio device. The loaded audio and waveform remain available.");
    });

    private void ReferenceSession_PlaybackStateChanged(MediaPlaybackSession sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || !ReferenceEquals(sender, _referencePlayer?.PlaybackSession)) { return; }
        _referencePlaying = sender.PlaybackState == MediaPlaybackState.Playing;
        UpdateReferenceRendering();
        RenderReferencePlaybackControls();
        UpdateReferencePosition();
    });

    private void ReferenceSession_SeekCompleted(MediaPlaybackSession sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || !ReferenceEquals(sender, _referencePlayer?.PlaybackSession)) { return; }
        _referenceSeeking = false;
        UpdateReferencePosition();
    });

    private void UpdateReferenceRendering()
    {
        bool needed = _referenceOpened && !_closing &&
            (_referencePlaying || _referenceSeekPointerId is not null || _referencePendingSeekSeconds is not null);
        if (needed == _referenceRendering) { return; }
        _referenceRendering = needed;
        if (needed) { CompositionTarget.Rendering += ReferencePlayback_Rendering; }
        else { CompositionTarget.Rendering -= ReferencePlayback_Rendering; }
    }

    private void ReferencePlayback_Rendering(object? sender, object args)
    {
        // Pointer events may arrive faster than frames. Submit only the latest target
        // once per frame rather than queueing a native seek for every mouse movement.
        if (_referencePendingSeekSeconds is { } seconds) { DisplayReferencePosition(seconds); }
        FlushReferenceSeek();
        UpdateReferencePosition();
        UpdateReferenceRendering();
    }

    private void ReferencePlayPause_Click(object sender, RoutedEventArgs args)
    {
        if (!_referenceOpened || _referencePlayer is null || _preparedReference is null || _referenceLoading || _closing) { return; }
        try
        {
            if (!_referenceEnded && _referencePlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) { _referencePlayer.Pause(); }
            else
            {
                if (_referenceEnded || _referencePlayer.PlaybackSession.Position >= _preparedReference.Duration)
                { _referenceEnded = false; _referencePlayer.PlaybackSession.Position = TimeSpan.Zero; }
                _referencePlayer.Play();
            }
            UpdateReferencePosition();
            RenderReferencePlaybackControls();
        }
        catch (Exception) { Notify("Windows could not play this audio. Check the audio device and try again."); }
    }

    private bool CanSeekReference => _referenceOpened && _referencePlayer is not null && _preparedReference is not null &&
        !_referenceLoading && !_closing;

    private void ReferenceWaveform_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!CanSeekReference || _referenceSeekPointerId is not null) { return; }
        var point = args.GetCurrentPoint(ReferenceWaveformSurface);
        if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) { return; }
        if (ReferenceWaveformInput.CapturePointer(args.Pointer)) { _referenceSeekPointerId = args.Pointer.PointerId; }
        SeekReferenceAtPointer(args);
        UpdateReferenceRendering();
        args.Handled = true;
    }

    private void ReferenceWaveform_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_referenceSeekPointerId != args.Pointer.PointerId) { return; }
        if (CanSeekReference) { SeekReferenceAtPointer(args); }
        else { EndReferenceSeeking(); }
        args.Handled = true;
    }

    private void ReferenceWaveform_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_referenceSeekPointerId != args.Pointer.PointerId) { return; }
        if (CanSeekReference) { SeekReferenceAtPointer(args); }
        if (_referencePendingSeekSeconds is { } seconds) { DisplayReferencePosition(seconds); }
        FlushReferenceSeek(); // Commit the exact release position, including while paused.
        EndReferenceSeeking();
        args.Handled = true;
    }

    private void ReferenceWaveform_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_referenceSeekPointerId != args.Pointer.PointerId) { return; }
        if (_referencePendingSeekSeconds is { } seconds) { DisplayReferencePosition(seconds); }
        FlushReferenceSeek();
        EndReferenceSeeking();
        UpdateReferencePosition();
        args.Handled = true;
    }

    private void ReferenceWaveform_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_referenceSeekPointerId == args.Pointer.PointerId)
        {
            if (_referencePendingSeekSeconds is { } seconds) { DisplayReferencePosition(seconds); }
            FlushReferenceSeek();
            _referenceSeekPointerId = null;
            UpdateReferenceRendering();
        }
    }

    private void EndReferenceSeeking()
    {
        _referenceSeekPointerId = null;
        ReferenceWaveformInput.ReleasePointerCaptures();
        UpdateReferenceRendering();
    }

    private void SeekReferenceAtPointer(PointerRoutedEventArgs args)
    {
        if (!CanSeekReference || ReferenceWaveformSurface.ActualWidth <= 0) { return; }
        double x = args.GetCurrentPoint(ReferenceWaveformSurface).Position.X;
        double seconds = Math.Clamp(x / ReferenceWaveformSurface.ActualWidth, 0, 1) * _preparedReference!.Duration.TotalSeconds;
        _referencePendingSeekSeconds = seconds;
        // Windows can clamp an end seek to the last sample. Keep the exact-end
        // intent so the displayed duration and the next Play action agree.
        _referenceEnded = seconds >= _preparedReference.Duration.TotalSeconds;
        _referencePositionSeconds = seconds;
        UpdateReferenceRendering();
    }

    private void FlushReferenceSeek()
    {
        if (_referenceSeeking || _referencePendingSeekSeconds is not { } seconds) { return; }
        _referencePendingSeekSeconds = null;
        if (!CanSeekReference) { return; }
        try
        {
            var session = _referencePlayer!.PlaybackSession;
            var position = TimeSpan.FromSeconds(seconds);
            if (session.Position == position) { return; }
            _referenceSeeking = true;
            session.Position = position;
        }
        catch (Exception)
        {
            _referenceSeeking = false;
            EndReferenceSeeking();
            Notify("Windows could not seek this preview. Try playing it again.");
        }
    }

    private void ReferenceWaveform_Loaded(object sender, RoutedEventArgs args)
    {
        var root = ReferenceWaveformSurface.XamlRoot;
        if (ReferenceEquals(root, _referenceWaveformRoot)) { return; }
        if (_referenceWaveformRoot is not null) { _referenceWaveformRoot.Changed -= ReferenceWaveformRoot_Changed; }
        _referenceWaveformRoot = root;
        if (root is not null) { root.Changed += ReferenceWaveformRoot_Changed; }
        DrawReferenceWaveform();
    }

    private void ReferenceWaveformRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => DrawReferenceWaveform();

    private void ReferenceWaveform_SizeChanged(object sender, SizeChangedEventArgs args) => DrawReferenceWaveform();

    private void DrawReferenceWaveform()
    {
        if (_preparedReference is null || ReferenceWaveformSurface.ActualWidth <= 0 || ReferenceWaveformSurface.ActualHeight <= 0) { return; }
        try
        {
            double width = ReferenceWaveformSurface.ActualWidth, height = ReferenceWaveformSurface.ActualHeight;
            double rasterScale = ReferenceWaveformSurface.XamlRoot?.RasterizationScale ?? 1;
            if (ReferenceEquals(_referenceWaveformOwner, _preparedReference) && _referenceWaveformWidth == width &&
                _referenceWaveformHeight == height && _referenceWaveformScale == rasterScale && ReferenceWaveformPath.Data is not null)
            { SetReferencePlayhead(_referencePositionSeconds); return; }

            var envelope = AudioWaveformDisplay.Create(_preparedReference.Waveform, width);
            int columns = envelope.Length;
            var geometry = new GeometryGroup();
            var playedGeometry = new GeometryGroup(); // Each WinUI Path owns its geometry tree.
            double scale = (height / 2 - 2) / Math.Max(1, _preparedReference.Peak);
            for (int column = 0; column < columns; column++)
            {
                double x = (column + 0.5) * width / columns;
                var start = new Point(x, height / 2 - envelope[column].Maximum * scale);
                var end = new Point(x, height / 2 - envelope[column].Minimum * scale);
                geometry.Children.Add(new LineGeometry { StartPoint = start, EndPoint = end });
                playedGeometry.Children.Add(new LineGeometry { StartPoint = start, EndPoint = end });
            }
            ReferenceWaveformPath.StrokeThickness = ReferencePlayedWaveform.StrokeThickness = 1;
            ReferenceWaveformPath.Data = geometry;
            ReferencePlayedWaveform.Data = playedGeometry;
            _referenceWaveformOwner = _preparedReference;
            _referenceWaveformWidth = width; _referenceWaveformHeight = height; _referenceWaveformScale = rasterScale;
            ReferencePlayhead.Visibility = Visibility.Visible;
            SetReferencePlayhead(_referencePositionSeconds);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // SizeChanged runs outside the awaited import operation. Report rendering errors
            // here rather than letting a later layout pass terminate the application.
            ReferenceWaveformPath.Data = null;
            ReferencePlayedWaveform.Data = null;
            ReferencePlayhead.Visibility = Visibility.Collapsed;
            Notify("The waveform could not be displayed. The loaded audio remains available.", true);
        }
    }

    private void UpdateReferencePosition()
    {
        if (_preparedReference is null || _referenceSeekPointerId is not null ||
            _referencePendingSeekSeconds is not null || _referenceSeeking) { return; }
        double seconds;
        try
        {
            seconds = _referenceEnded ? _preparedReference.Duration.TotalSeconds : Math.Clamp(_referenceOpened && _referencePlayer is not null
                ? _referencePlayer.PlaybackSession.Position.TotalSeconds : 0, 0, _preparedReference.Duration.TotalSeconds);
        }
        catch (Exception)
        {
            ReleasePlayback();
            RenderReferencePlaybackControls();
            Notify("Windows playback is unavailable. The audio remains loaded.");
            return;
        }
        DisplayReferencePosition(seconds);
    }

    private void DisplayReferencePosition(double seconds)
    {
        _referencePositionSeconds = seconds;
        string time = FormatReferenceTime(TimeSpan.FromSeconds(seconds));
        if (ReferenceCurrentTime.Text != time) { ReferenceCurrentTime.Text = time; }
        SetReferencePlayhead(seconds);
    }

    private void SetReferencePlayhead(double seconds)
    {
        if (_preparedReference is null) { return; }
        double width = ReferenceWaveformSurface.ActualWidth, height = ReferenceWaveformSurface.ActualHeight;
        double x = width * Math.Clamp(seconds / _preparedReference.Duration.TotalSeconds, 0, 1);
        _referencePlayedClip.Rect = new Rect(0, 0, x, height);
        _referencePlayheadTransform.X = Math.Clamp(x, 0, Math.Max(0, width - ReferencePlayhead.StrokeThickness));
        if (ReferencePlayhead.Y2 != height) { ReferencePlayhead.Y2 = height; }
    }

    private static string FormatReferenceTime(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds / 10:00}";

    private void RenderReferencePlaybackControls()
    {
        bool enabled = CanSeekReference;
        ReferencePlayButton.IsEnabled = enabled;
        ReferenceWaveformInput.IsHitTestVisible = enabled;
        bool playing = false;
        try { playing = enabled && !_referenceEnded && _referencePlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing; }
        catch (Exception) { ReferencePlayButton.IsEnabled = ReferenceWaveformInput.IsHitTestVisible = false; }
        ReferencePlayIcon.Symbol = playing ? Symbol.Pause : Symbol.Play;
        string action = playing ? $"Pause {AudioLabel}" : $"Play {AudioLabel}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReferencePlayButton, action);
        ToolTipService.SetToolTip(ReferencePlayButton, action);
    }

    internal void ReleasePlayback()
    {
        _referencePlaying = false;
        UpdateReferenceRendering();
        _referenceOpened = false;
        _referencePendingSeekSeconds = null;
        _referenceSeeking = false;
        _referenceWaveformOwner = null;
        EndReferenceSeeking();
        bool incomplete = false;
        void Release(Action action) { try { action(); } catch (Exception) { incomplete = true; } }
        if (_referencePlayer is { } player)
        {
            _referencePlayer = null;
            Release(() =>
            {
                player.MediaOpened -= ReferencePlayer_MediaOpened;
                player.MediaEnded -= ReferencePlayer_MediaEnded;
                player.MediaFailed -= ReferencePlayer_MediaFailed;
                player.PlaybackSession.PlaybackStateChanged -= ReferenceSession_PlaybackStateChanged;
                player.PlaybackSession.SeekCompleted -= ReferenceSession_SeekCompleted;
                player.Pause(); player.Source = null;
            });
            Release(player.Dispose);
        }
        if (_referencePlaybackSource is { } source) { Release(source.Dispose); _referencePlaybackSource = null; }
        if (incomplete && _preparedReference is PreparedReferenceAudio reference) { reference.PreserveTemporaryFiles = true; }
    }

}
