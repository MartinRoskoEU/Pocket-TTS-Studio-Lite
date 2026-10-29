using Microsoft.UI.Xaml;
using System.Net.Http;
using PocketTTSStudioLite.Services.Authentication;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient _authenticationHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly HuggingFaceAuthenticationService _authentication;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        InitializeShell();
        _authentication = new HuggingFaceAuthenticationService(_authenticationHttp);
        Closed += MainWindow_Closed;
        InitializeVoiceExport();
        InitializeRuntimeUpdates();
        InitializeModelInstallation();
        InitializeModelSetup();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _generationCancellation?.Cancel();
        VoicePreviewPlayer.Dispose();
        _previewAudio = null;
        CloseReferenceAudioFallback();
        _exportCancellation?.Cancel();
        _setupCancellation.Cancel();
        _runtimeInstallCancellation?.Cancel();
        _modelInstallCancellation?.Cancel();
        _modelRemovalCancellation?.Cancel();
        _modelInstallHttp.Dispose();
        _modelUpdateHttp.Dispose();
        _runtimeUpdateHttp.Dispose();
        _runtimeDownloadHttp.Dispose();
        _authentication.Dispose();
        _authenticationHttp.Dispose();
    }
}
