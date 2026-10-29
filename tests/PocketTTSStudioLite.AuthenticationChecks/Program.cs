using System.Net;
using System.Text;
using System.Text.Json;
using PocketTTSStudioLite.Configuration;
using PocketTTSStudioLite.Services.Authentication;

int passed = 0;
bool tokenHandoffOnly = args.Contains("--token-handoff");
await Verify("Public client/scopes, pending authorization, slower polling, and safe session state", async () =>
{
    var time = new TestTimeProvider();
    var pollTimes = new List<DateTimeOffset>();
    int polls = 0;
    using var client = Mock(async (request, _) =>
    {
        Dictionary<string, string> form = await ValidateRequest(request);
        if (request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint)
        {
            Check(form["scope"] == "openid gated-repos", "Required scopes changed.");
            return Device();
        }
        Check(form["grant_type"] == "urn:ietf:params:oauth:grant-type:device_code" && form["device_code"] == "fixture-device",
            "Incorrect token grant.");
        pollTimes.Add(time.GetUtcNow());
        return ++polls switch { 1 => Error("authorization_pending"), 2 => Error("slow_down"), _ => Token() };
    });
    using var auth = new HuggingFaceAuthenticationService(client, time);
    await Pump(auth.SignInAsync(), time);
    Check(auth.IsSignedIn && auth.Status.State == HuggingFaceAuthenticationState.SignedIn, "Session not established.");
    Check(auth.GetAccessTokenForProcess() == "fixture-access", "Current in-memory token was unavailable for the child-process handoff.");
    Check(pollTimes[1] - pollTimes[0] >= TimeSpan.FromSeconds(1) && pollTimes[2] - pollTimes[1] >= TimeSpan.FromSeconds(6),
        "Polling ignored the minimum interval or slow_down increment.");
    string status = JsonSerializer.Serialize(auth.Status);
    Check(!status.Contains("fixture-access") && !status.Contains("fixture-device") && !status.Contains("fixture-id"),
        "Credentials leaked into UI-safe status.");
    auth.SignOut();
    Check(!auth.IsSignedIn && auth.Status.State == HuggingFaceAuthenticationState.SignedOut && auth.Status.UserCode is null,
        "Sign-out did not clear the session/code.");
    Check(auth.GetAccessTokenForProcess() is null, "Sign-out retained a child-process credential.");
}, tokenHandoff: true);

await Verify("Authorization rejection and server/local code expiration", async () =>
{
    foreach (string outcome in new[] { "access_denied", "expired_token", "local-expiry" })
    {
        var time = new TestTimeProvider();
        using var client = Mock((request, _) => Task.FromResult(request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint
            ? Device(outcome == "local-expiry" ? 3 : 60)
            : Error(outcome == "local-expiry" ? "authorization_pending" : outcome)));
        using var auth = new HuggingFaceAuthenticationService(client, time);
        await Pump(auth.SignInAsync(), time);
        Check(auth.Status.State == (outcome == "access_denied" ? HuggingFaceAuthenticationState.Rejected : HuggingFaceAuthenticationState.Expired)
            && !auth.IsSignedIn && auth.Status.UserCode is null, "Authorization outcome was not handled safely.");
    }
});

await Verify("Caller cancellation and cancel-sign-in clear pending state", async () =>
{
    foreach (bool external in new[] { true, false })
    {
        var time = new TestTimeProvider();
        using var client = Mock((_, _) => Task.FromResult(Device()));
        using var auth = new HuggingFaceAuthenticationService(client, time);
        using var cancellation = new CancellationTokenSource();
        Task operation = auth.SignInAsync(cancellation.Token);
        if (external) { cancellation.Cancel(); } else { auth.CancelSignIn(); }
        var exception = await Expect<OperationCanceledException>(operation);
        Check(!external || exception.CancellationToken == cancellation.Token, "Caller cancellation token was lost.");
        Check(auth.Status.State == HuggingFaceAuthenticationState.Cancelled && !auth.IsSignedIn && auth.Status.UserCode is null,
            "Cancellation left an active sign-in/session.");
    }
});

await Verify("Sign-out and disposal prevent late token responses from restoring a session", async () =>
{
    foreach (bool dispose in new[] { false, true })
    {
        var time = new TestTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Mock((request, _) =>
        {
            if (request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint) { return Task.FromResult(Device()); }
            started.TrySetResult();
            return delayedResponse.Task; // Deliberately ignore cancellation to exercise a late HTTP completion.
        });
        using var auth = new HuggingFaceAuthenticationService(client, time);
        Task operation = auth.SignInAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) { auth.Dispose(); } else { auth.SignOut(); }
        delayedResponse.SetResult(Token());
        await Expect<OperationCanceledException>(operation);
        Check(!auth.IsSignedIn && auth.Status.State == HuggingFaceAuthenticationState.SignedOut,
            "A late response restored a cleared session.");
    }
});

await Verify("Session expiration clears the token and updates status", async () =>
{
    var time = new TestTimeProvider();
    using var client = Mock((request, _) => Task.FromResult(request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint ? Device() : Token()));
    using var auth = new HuggingFaceAuthenticationService(client, time);
    await Pump(auth.SignInAsync(), time);
    time.Advance(TimeSpan.FromSeconds(3601));
    Check(!auth.IsSignedIn && auth.Status.State == HuggingFaceAuthenticationState.Expired, "Expired session was retained.");
    Check(auth.GetAccessTokenForProcess() is null, "Expired token remained available for child-process handoff.");
}, tokenHandoff: true);

await Verify("Polling network failures back off; initial connectivity failure is explicit", async () =>
{
    var time = new TestTimeProvider();
    int polls = 0;
    var pollTimes = new List<DateTimeOffset>();
    bool retryStatusSeen = false;
    using var client = Mock((request, _) =>
    {
        if (request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint) { return Task.FromResult(Device()); }
        pollTimes.Add(time.GetUtcNow());
        if (++polls == 1) { throw new HttpRequestException("fixture connection failure"); }
        return Task.FromResult(Token());
    });
    using var auth = new HuggingFaceAuthenticationService(client, time);
    auth.StatusChanged += (_, _) => retryStatusSeen |= auth.Status.Message.Contains("Connection interrupted");
    await Pump(auth.SignInAsync(), time);
    Check(auth.IsSignedIn && retryStatusSeen && pollTimes[1] - pollTimes[0] >= TimeSpan.FromSeconds(2), "Network retry did not back off.");
    using var offlineClient = Mock((_, _) => throw new HttpRequestException("fixture offline"));
    using var offline = new HuggingFaceAuthenticationService(offlineClient, time);
    await offline.SignInAsync();
    Check(offline.Status.State == HuggingFaceAuthenticationState.Failed && !offline.IsSignedIn, "Offline startup failure was hidden.");
});

await Verify("Invalid registration, malformed responses, and incomplete permissions are rejected without secret output", async () =>
{
    foreach (string scenario in new[] { "invalid_client", "invalid_scope", "bad-uri", "bad-lifetime", "bad-json", "bad-token", "bad-scope" })
    {
        var time = new TestTimeProvider();
        using var client = Mock((request, _) => Task.FromResult(request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint
            ? scenario switch
            {
                "invalid_client" or "invalid_scope" => Error(scenario),
                "bad-uri" => Json("{\"device_code\":\"fixture-device\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://example.com/auth\",\"expires_in\":60}"),
                "bad-lifetime" => Json("{\"device_code\":\"fixture-device\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://huggingface.co/oauth/device\",\"expires_in\":\"wrong\"}"),
                "bad-json" => Json("not json"),
                _ => Device(),
            }
            : scenario == "bad-token" ? Json("{\"access_token\":\"fixture-access\",\"token_type\":\"wrong\",\"expires_in\":3600}")
            : Token("openid")));
        using var auth = new HuggingFaceAuthenticationService(client, time);
        await Pump(auth.SignInAsync(), time);
        Check(auth.Status.State == HuggingFaceAuthenticationState.Failed && !auth.IsSignedIn &&
            !auth.Status.Message.Contains("fixture-access") && !auth.Status.Message.Contains("fixture-device"), "Invalid response was accepted or leaked.");
    }
});

await Verify("Default polling interval and per-request timeout are enforced", async () =>
{
    var time = new TestTimeProvider();
    DateTimeOffset? polledAt = null;
    DateTimeOffset start = time.GetUtcNow();
    using var client = Mock((request, _) =>
    {
        if (request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint)
        { return Task.FromResult(Json("{\"device_code\":\"fixture-device\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://huggingface.co/oauth/device\",\"expires_in\":60}")); }
        polledAt = time.GetUtcNow();
        return Task.FromResult(Token());
    });
    using var auth = new HuggingFaceAuthenticationService(client, time);
    await Pump(auth.SignInAsync(), time);
    Check(polledAt - start >= TimeSpan.FromSeconds(5), "Default polling interval was not respected.");
    var timeoutTime = new TestTimeProvider();
    using var stalledClient = Mock(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Device(); });
    using var stalled = new HuggingFaceAuthenticationService(stalledClient, timeoutTime);
    await Pump(stalled.SignInAsync(), timeoutTime);
    Check(stalled.Status.State == HuggingFaceAuthenticationState.Failed, "Initial request timeout was not surfaced.");
});

if (args.Contains("--live-device"))
{
    await Verify("Official public client accepts the device request; cancelled before browser or polling", async () =>
    {
        using var guard = new LiveDeviceOnlyHandler { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false } };
        using var client = new HttpClient(guard);
        using var auth = new HuggingFaceAuthenticationService(client);
        bool received = false;
        auth.StatusChanged += (_, _) =>
        {
            if (auth.Status.State == HuggingFaceAuthenticationState.WaitingForAuthorization)
            {
                received = !string.IsNullOrEmpty(auth.Status.UserCode) && auth.Status.VerificationUri?.Host == "huggingface.co";
                auth.CancelSignIn();
            }
        };
        try { await auth.SignInAsync(); } catch (OperationCanceledException) { }
        Check(received && guard.RequestCount == 1 && !auth.IsSignedIn,
            "Live device request did not succeed: " + auth.Status.Message);
        // Never print, persist, or authorize the returned codes. The pending request expires server-side.
    });
}

Console.WriteLine($"{passed} targeted authentication check groups passed.");

async Task Verify(string name, Func<Task> test, bool tokenHandoff = false)
{
    if (tokenHandoffOnly && !tokenHandoff) { return; }
    await test();
    passed++;
    Console.WriteLine("PASS " + name);
}
static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
static async Task<T> Expect<T>(Task operation) where T : Exception
{
    try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (T exception) { return exception; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static async Task Pump(Task operation, TestTimeProvider time)
{
    for (int step = 0; step < 150 && !operation.IsCompleted; step++)
    { time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(2); }
    await operation.WaitAsync(TimeSpan.FromSeconds(5));
}
static HttpClient Mock(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new StubHandler(handler));
static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{ Content = new StringContent(json, Encoding.UTF8, "application/json") };
static HttpResponseMessage Device(int lifetime = 60) => Json(JsonSerializer.Serialize(new
{ device_code = "fixture-device", user_code = "ABCD-EFGH", verification_uri = "https://hf.co/oauth/device", expires_in = lifetime, interval = 1 }));
static HttpResponseMessage Token(string scopes = "openid gated-repos") => Json(JsonSerializer.Serialize(new
{ access_token = "fixture-access", token_type = "Bearer", expires_in = 3600, scope = scopes, id_token = "fixture-id", refresh_token = "fixture-refresh" }));
static HttpResponseMessage Error(string error) => Json(JsonSerializer.Serialize(new { error, error_description = "Do not display raw provider payloads." }), HttpStatusCode.BadRequest);
static async Task<Dictionary<string, string>> ValidateRequest(HttpRequestMessage request)
{
    Check(request.Method == HttpMethod.Post && request.Headers.Authorization is null &&
        (request.RequestUri == HuggingFaceOAuthConfiguration.DeviceEndpoint || request.RequestUri == HuggingFaceOAuthConfiguration.TokenEndpoint), "Unexpected OAuth endpoint/method/header.");
    string body = await request.Content!.ReadAsStringAsync();
    var form = body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair =>
        Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    Check(form["client_id"] == "e5f65004-0823-403e-8c4a-1fd4f23db279" && !form.ContainsKey("client_secret"), "Incorrect public client configuration.");
    return form;
}

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token);
}
sealed class LiveDeviceOnlyHandler : DelegatingHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri != HuggingFaceOAuthConfiguration.DeviceEndpoint || request.Method != HttpMethod.Post || request.Headers.Authorization is not null)
        { throw new Exception("Live verification permits only the public device-code request."); }
        RequestCount++;
        return base.SendAsync(request, token);
    }
}

sealed class TestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private readonly List<TestTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    public void Advance(TimeSpan duration)
    {
        TestTimer[] due;
        lock (_gate)
        {
            _now += duration;
            due = _timers.Where(timer => timer.DueAt <= _now).ToArray();
            foreach (TestTimer timer in due) { timer.DueAt = null; }
        }
        foreach (TestTimer timer in due) { timer.Fire(); }
    }
    private sealed class TestTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed) { return false; }
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }
        }
        public void Fire() { if (!_disposed) { callback(state); } }
        public void Dispose() { lock (owner._gate) { _disposed = true; DueAt = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
