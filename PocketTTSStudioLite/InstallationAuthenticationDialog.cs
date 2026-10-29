using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PocketTTSStudioLite.Services.Authentication;

namespace PocketTTSStudioLite;

/// <summary>Installation-only presentation of the existing OAuth device flow. Never receives a token.</summary>
internal sealed class InstallationAuthenticationDialog : ContentDialog
{
    private readonly HuggingFaceAuthenticationService _authentication;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _progress = new() { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _authorization = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBox _code = new() { IsReadOnly = true, FontSize = 24 };
    private readonly TextBlock _expiry = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _address = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _browserStatus = new() { TextWrapping = TextWrapping.Wrap };
    private string? _openedUserCode;
    private bool _active;

    internal InstallationAuthenticationDialog(HuggingFaceAuthenticationService authentication)
    {
        _authentication = authentication;
        Title = "Authorize model installation";
        CloseButtonText = "Cancel installation";
        DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_code, "Hugging Face authorization code");
        var openBrowser = new Button { Content = "Open authorization page" };
        openBrowser.Click += async (_, _) => await OpenAuthorizationPageAsync();
        _authorization.Children.Add(new TextBlock { Text = "Enter this code on Hugging Face:", TextWrapping = TextWrapping.Wrap });
        _authorization.Children.Add(_code);
        _authorization.Children.Add(_expiry);
        _authorization.Children.Add(_address);
        _authorization.Children.Add(openBrowser);
        _authorization.Children.Add(_browserStatus);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Hugging Face authorization is required to download this model. Complete sign-in in your browser.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Signing in does not automatically accept the repository conditions. Review and accept them on Hugging Face when required, then complete authorization.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new HyperlinkButton
        {
            Content = "Review kyutai/pocket-tts repository conditions",
            NavigateUri = new Uri("https://huggingface.co/kyutai/pocket-tts"),
        });
        content.Children.Add(_progress);
        content.Children.Add(_status);
        content.Children.Add(_authorization);
        Content = content;
    }

    internal async Task<HuggingFaceAuthenticationStatus> AuthorizeAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        bool authorized = false;
        Task<ContentDialogResult>? shown = null;
        _active = true;
        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => opened.TrySetResult();
        void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (!finished) { cancellation.Cancel(); }
        }
        void OnStatusChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_active || cancellation.IsCancellationRequested) { return; }
            RenderStatus();
        });
        Opened += OnOpened;
        Closing += OnClosing;
        _authentication.StatusChanged += OnStatusChanged;
        using var cancelDialog = cancellation.Token.Register(() =>
        {
            _authentication.SignOut();
            DispatcherQueue.TryEnqueue(() => { if (_active) { Hide(); } });
        });
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            shown = ShowAsync().AsTask();
            await Task.WhenAny(opened.Task, shown);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!opened.Task.IsCompleted)
            {
                await shown;
                return new(HuggingFaceAuthenticationState.Cancelled, "Model installation cancelled during authorization.");
            }
            await _authentication.SignInAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var status = _authentication.Status;
            authorized = status.State == HuggingFaceAuthenticationState.SignedIn && _authentication.IsSignedIn;
            return status;
        }
        catch (OperationCanceledException)
        { return new(HuggingFaceAuthenticationState.Cancelled, "Model installation cancelled during authorization."); }
        catch (Exception)
        {
            // Neither platform exceptions nor OAuth payloads are safe to display.
            return new(HuggingFaceAuthenticationState.Failed, "Authorization could not complete. Retry the installation.");
        }
        finally
        {
            finished = true;
            _active = false;
            _authentication.StatusChanged -= OnStatusChanged;
            Opened -= OnOpened;
            Closing -= OnClosing;
            if (!authorized) { _authentication.SignOut(); }
            if (shown is not null)
            {
                Hide();
                try { await shown; }
                catch (Exception) { _authentication.SignOut(); }
            }
        }
    }

    private void RenderStatus()
    {
        var status = _authentication.Status;
        bool pending = status.State is HuggingFaceAuthenticationState.RequestingCode or HuggingFaceAuthenticationState.WaitingForAuthorization;
        _status.Text = status.Message;
        _progress.IsActive = pending;
        _progress.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        _authorization.Visibility = status.UserCode is null ? Visibility.Collapsed : Visibility.Visible;
        _code.Text = status.UserCode ?? "";
        _address.Text = status.VerificationUri?.AbsoluteUri ?? "";
        _expiry.Text = status.DeviceCodeExpiresAtUtc is { } expires ? $"Code expires at {expires.ToLocalTime():T}." : "";
        if (status.State == HuggingFaceAuthenticationState.WaitingForAuthorization && status.UserCode != _openedUserCode)
        {
            _openedUserCode = status.UserCode;
            _ = OpenAuthorizationPageAsync();
        }
    }

    private async Task OpenAuthorizationPageAsync()
    {
        var status = _authentication.Status;
        if (!_active || status.State != HuggingFaceAuthenticationState.WaitingForAuthorization || status.VerificationUri is null) { return; }
        bool opened = false;
        try { opened = await Windows.System.Launcher.LaunchUriAsync(status.VerificationUri); }
        catch (Exception) { /* Keep the public address/code fallback without exposing platform payloads. */ }
        if (_active && _authentication.Status.UserCode == status.UserCode)
        {
            _browserStatus.Text = opened ? "Browser opened. Enter the code and review the requested permissions."
                : "Could not open the browser. Open the address above manually and enter the code.";
        }
    }
}
