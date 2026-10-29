using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PocketTTSStudioLite.Configuration;

namespace PocketTTSStudioLite.Services.Authentication;

/// <summary>Public OAuth device flow with a process-memory-only session and no credential logging.</summary>
public sealed class HuggingFaceAuthenticationService : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CancellationTokenSource? _login;
    private ITimer? _expiryTimer;
    private string? _accessToken;
    private DateTimeOffset? _sessionExpiresAt;
    private long _generation;
    private bool _disposed;
    private HuggingFaceAuthenticationStatus _status = new(HuggingFaceAuthenticationState.SignedOut, "Signed out.");

    public HuggingFaceAuthenticationService(HttpClient httpClient) : this(httpClient, TimeProvider.System) { }

    internal HuggingFaceAuthenticationService(HttpClient httpClient, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _http = httpClient;
        _time = timeProvider;
    }

    public event EventHandler? StatusChanged;

    public HuggingFaceAuthenticationStatus Status { get { lock (_gate) { return _status; } } }
    public bool IsSignedIn
    {
        get { lock (_gate) { return _accessToken is not null && _sessionExpiresAt > _time.GetUtcNow(); } }
    }

    /// <summary>Ephemeral credential handoff for owned operations; callers must never display, log, or persist the value.</summary>
    internal string? GetAccessTokenForProcess()
    {
        lock (_gate)
        {
            return !_disposed && _sessionExpiresAt > _time.GetUtcNow() ? _accessToken : null;
        }
    }

    public Task SignInAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource login;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_login is not null || _accessToken is not null)
            {
                throw new InvalidOperationException("Sign out or cancel the current sign-in before starting another.");
            }
            login = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _login = login;
            generation = ++_generation;
        }
        SetStatus(generation, new(HuggingFaceAuthenticationState.RequestingCode, "Requesting an authorization code…"));
        return RunSignInAsync(generation, login, cancellationToken);
    }

    public void CancelSignIn() => ClearSession(HuggingFaceAuthenticationState.Cancelled, "Sign-in cancelled.", pendingOnly: true);
    public void SignOut() => ClearSession(HuggingFaceAuthenticationState.SignedOut, "Signed out. The local session has been cleared.");

    private async Task RunSignInAsync(long generation, CancellationTokenSource login, CancellationToken callerToken)
    {
        try
        {
            DateTimeOffset requestedAt = _time.GetUtcNow();
            var response = await PostAsync(HuggingFaceOAuthConfiguration.DeviceEndpoint,
                new Dictionary<string, string> { ["client_id"] = HuggingFaceOAuthConfiguration.ClientId,
                    ["scope"] = HuggingFaceOAuthConfiguration.Scopes }, login.Token).ConfigureAwait(false);
            using JsonDocument deviceJson = response.Json;
            if (!IsSuccess(response.Status)) { throw ProtocolFailure(deviceJson.RootElement); }
            JsonElement device = deviceJson.RootElement;
            string deviceCode = RequiredString(device, "device_code");
            string userCode = RequiredString(device, "user_code");
            Uri verificationUri = ValidateVerificationUri(RequiredString(device, "verification_uri"));
            // The base URI plus visible user code works without exposing private codes to a browser URL.
            DateTimeOffset expiresAt = requestedAt.AddSeconds(PositiveSeconds(device, "expires_in", 86400));
            double interval = device.TryGetProperty("interval", out _)
                ? PositiveSeconds(device, "interval", 86400) : 5;
            if (expiresAt <= _time.GetUtcNow()) { SetExpired(generation); return; }
            var waiting = new HuggingFaceAuthenticationStatus(HuggingFaceAuthenticationState.WaitingForAuthorization,
                "Waiting for you to authorize Pocket TTS Studio lite in your browser.", userCode, verificationUri, expiresAt);
            SetStatus(generation, waiting);
            login.Token.ThrowIfCancellationRequested();
            TimeSpan remaining = expiresAt - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) { SetExpired(generation); return; }
            using var expiration = new CancellationTokenSource(remaining, _time);
            using var polling = CancellationTokenSource.CreateLinkedTokenSource(login.Token, expiration.Token);
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), _time, polling.Token).ConfigureAwait(false);
                    if (_time.GetUtcNow() >= expiresAt) { SetExpired(generation); return; }
                    (HttpStatusCode Status, JsonDocument Json) tokenResponse;
                    try
                    {
                        tokenResponse = await PostAsync(HuggingFaceOAuthConfiguration.TokenEndpoint,
                            new Dictionary<string, string> { ["client_id"] = HuggingFaceOAuthConfiguration.ClientId,
                                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                                ["device_code"] = deviceCode }, polling.Token).ConfigureAwait(false);
                    }
                    catch (OAuthTransportException)
                    {
                        interval = Math.Min(interval * 2, 86400);
                        SetStatus(generation, waiting with { Message = "Connection interrupted. Retrying authorization more slowly until the code expires…" });
                        continue;
                    }

                    using JsonDocument tokenJson = tokenResponse.Json;
                    JsonElement token = tokenJson.RootElement;
                    string? error = OptionalError(token);
                    if (error is null && IsSuccess(tokenResponse.Status))
                    {
                        polling.Token.ThrowIfCancellationRequested();
                        if (_time.GetUtcNow() >= expiresAt) { SetExpired(generation); return; }
                        string accessToken = RequiredString(token, "access_token");
                        if (!RequiredString(token, "token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                        { throw new OAuthProtocolException("Hugging Face returned an unsupported token type."); }
                        int lifetime = PositiveSeconds(token, "expires_in", 31 * 86400);
                        string scope = token.TryGetProperty("scope", out _) ? RequiredString(token, "scope") : HuggingFaceOAuthConfiguration.Scopes;
                        string[] granted = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (!granted.Contains("openid", StringComparer.Ordinal) || !granted.Contains("gated-repos", StringComparer.Ordinal))
                        { throw new OAuthProtocolException("The required openid and gated-repos permissions were not granted. Please sign in again."); }
                        InstallSession(generation, polling.Token, accessToken, _time.GetUtcNow().AddSeconds(lifetime));
                        return;
                    }

                    switch (error)
                    {
                        case "authorization_pending":
                            SetStatus(generation, waiting);
                            break;
                        case "slow_down":
                            interval = Math.Min(interval + 5, 86400);
                            SetStatus(generation, waiting with { Message = "Waiting for authorization. Hugging Face requested slower polling." });
                            break;
                        case "access_denied":
                            SetStatus(generation, new(HuggingFaceAuthenticationState.Rejected, "Authorization was rejected. You can sign in again when ready."));
                            return;
                        case "expired_token":
                            SetExpired(generation);
                            return;
                        default:
                            throw ProtocolFailure(token);
                    }
                }
            }
            catch (OperationCanceledException) when (expiration.IsCancellationRequested && !login.IsCancellationRequested)
            {
                SetExpired(generation);
            }
        }
        catch (OperationCanceledException) when (login.IsCancellationRequested)
        {
            SetStatus(generation, new(HuggingFaceAuthenticationState.Cancelled, "Sign-in cancelled."));
            throw new OperationCanceledException("Hugging Face sign-in was cancelled.", callerToken.IsCancellationRequested ? callerToken : login.Token);
        }
        catch (OAuthTransportException)
        {
            SetStatus(generation, new(HuggingFaceAuthenticationState.Failed, "Could not connect to Hugging Face. Check your internet connection and sign in again."));
        }
        catch (OAuthProtocolException exception)
        {
            SetStatus(generation, new(HuggingFaceAuthenticationState.Failed, exception.Message));
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_login, login)) { _login = null; } }
            login.Dispose();
        }
    }

    private void InstallSession(long generation, CancellationToken cancellationToken, string accessToken, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) { return; }
            _accessToken = accessToken;
            _sessionExpiresAt = expiresAt;
            _status = new(HuggingFaceAuthenticationState.SignedIn, "Signed in to Hugging Face.", SessionExpiresAtUtc: expiresAt);
            _expiryTimer = _time.CreateTimer(_ => ExpireSession(generation), null,
                TimeSpan.FromSeconds(Math.Clamp((expiresAt - _time.GetUtcNow()).TotalSeconds, 0, 86400)), Timeout.InfiniteTimeSpan);
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExpireSession(long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _sessionExpiresAt is null) { return; }
            TimeSpan remaining = _sessionExpiresAt.Value - _time.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                _expiryTimer?.Change(TimeSpan.FromSeconds(Math.Min(remaining.TotalSeconds, 86400)), Timeout.InfiniteTimeSpan);
                return;
            }
            _accessToken = null;
            _sessionExpiresAt = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _status = new(HuggingFaceAuthenticationState.Expired, "Your Hugging Face session expired. Sign in again to continue.");
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetExpired(long generation) => SetStatus(generation,
        new(HuggingFaceAuthenticationState.Expired, "The authorization code expired. Sign in again to get a new code."));

    private void SetStatus(long generation, HuggingFaceAuthenticationStatus status)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) { return; }
            _status = status;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSession(HuggingFaceAuthenticationState state, string message, bool pendingOnly = false, bool dispose = false)
    {
        CancellationTokenSource? login;
        lock (_gate)
        {
            if (_disposed || (pendingOnly && _login is null)) { return; }
            if (dispose) { _disposed = true; }
            ++_generation;
            login = _login;
            _login = null;
            _accessToken = null;
            _sessionExpiresAt = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _status = new(state, message);
        }
        TryCancel(login);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        ClearSession(HuggingFaceAuthenticationState.SignedOut, "Signed out.", dispose: true);
        GC.SuppressFinalize(this);
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { /* Completion raced sign-out. */ }
    }

    private async Task<(HttpStatusCode Status, JsonDocument Json)> PostAsync(
        Uri endpoint, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(parameters) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, operation.Token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            { throw new OAuthTransportException(); }
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
            { throw new OAuthProtocolException("Hugging Face returned an unexpected authentication response. Please try signing in again."); }
            await response.Content.LoadIntoBufferAsync(65536, operation.Token).ConfigureAwait(false);
            byte[] content = await response.Content.ReadAsByteArrayAsync(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            JsonDocument json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            { json.Dispose(); throw new OAuthProtocolException("Hugging Face returned invalid authentication metadata."); }
            return (response.StatusCode, json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new OAuthTransportException(); }
        catch (HttpRequestException) { throw new OAuthTransportException(); }
        catch (IOException) { throw new OAuthTransportException(); }
        catch (JsonException) { throw new OAuthProtocolException("Hugging Face returned invalid authentication metadata."); }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;
    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        { throw new OAuthProtocolException("Hugging Face returned incomplete authentication metadata."); }
        return value.GetString()!;
    }
    private static int PositiveSeconds(JsonElement element, string name, int maximum)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int seconds) || seconds <= 0 || seconds > maximum)
        { throw new OAuthProtocolException("Hugging Face returned an invalid authentication lifetime or polling interval."); }
        return seconds;
    }
    private static string? OptionalError(JsonElement element) => element.TryGetProperty("error", out _) ? RequiredString(element, "error") : null;
    private static OAuthProtocolException ProtocolFailure(JsonElement element) => OptionalError(element) switch
    {
        "invalid_client" => new("Hugging Face rejected the public application registration. Verify its client ID and device-flow settings."),
        "invalid_scope" => new("Hugging Face rejected the required openid and gated-repos scopes. Check the OAuth application settings."),
        _ => new("Hugging Face could not authorize this application. Please start sign-in again."),
    };
    private static Uri ValidateVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        { throw new OAuthProtocolException("Hugging Face returned an invalid authorization page address."); }
        if (uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)) { return uri; }
        // The official device endpoint returns this short URL; it redirects to the canonical HF address.
        if (uri.Host.Equals("hf.co", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/oauth/device")
        { return new UriBuilder(uri) { Host = "huggingface.co" }.Uri; }
        throw new OAuthProtocolException("Hugging Face returned an invalid authorization page address.");
    }
    private sealed class OAuthTransportException : Exception;
    private sealed class OAuthProtocolException(string message) : Exception(message);
}
