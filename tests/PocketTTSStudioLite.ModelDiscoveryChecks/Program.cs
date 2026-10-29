using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PocketTTSStudioLite.Services;

const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string Blob = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const string LfsHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
int passed = 0;

await Verify("Dynamic variant discovery, exact revisions, and conservative capability states", async () =>
{
    using var client = Client((_, _) => Task.FromResult(JsonResponse(Fixture())));
    PocketTtsModelDiscoveryResult result = await new PocketTtsModelDiscoveryService(client).DiscoverAsync();
    Check(result.Models.Count == 2 && result.Files.Count == 4, "Unexpected candidate or inventory count.");
    PocketTtsDiscoveredModel model = result.Models.Single(model => model.Name == "future_variant_2031");
    Check(model.RepositoryPath == "languages/future_variant_2031" && model.Id.EndsWith(model.RepositoryPath), "Variant identity lost.");
    Check(result.Revision == Commit && result.Files.All(file => file.Revision == Commit), "Unpinned file metadata.");
    Check(result.Models.All(model => model.Revision == Commit &&
        model.RuntimeCompatibility == PocketTtsModelCompatibility.Unknown &&
        model.VoiceCloningSupport == PocketTtsVoiceCloningSupport.Unknown), "Discovery must not verify compatibility or cloning.");
    Check(model.CandidateWeightPaths.Count == 1 && model.Files.Count == 3, "Embeddings were classified as models or metadata was lost.");
    PocketTtsModelFile weight = model.Files.Single(file => file.RepositoryPath.EndsWith("/model.safetensors"));
    Check(weight.SizeBytes == 123 && weight.GitBlobId == Blob && weight.Lfs?.Sha256 == LfsHash &&
        weight.Lfs.PointerSizeBytes == 128, "File/LFS metadata was lost or conflated.");
    Check(model.Files.Single(file => file.RepositoryPath.EndsWith("tokenizer.json")).SizeBytes is null,
        "Missing metadata must remain unknown.");
});

await Verify("Revision escaping, pinned request matching, and no file-content requests", async () =>
{
    using var client = Client((request, _) =>
    {
        Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "Unexpected method or authentication.");
        string release = typeof(ApplicationInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Check(request.Headers.UserAgent.ToString() == $"PocketTTSStudioLite/{release}", "HTTP User-Agent must use the built release version.");
        Check(request.RequestUri!.AbsoluteUri ==
            "https://huggingface.co/api/models/kyutai/pocket-tts/revision/refs%2Fpr%2F12?blobs=true", "Incorrect official API URL.");
        return Task.FromResult(JsonResponse(Fixture()));
    });
    await new PocketTtsModelDiscoveryService(client).DiscoverAsync("refs/pr/12");
    using var mismatchClient = Client((_, _) => Task.FromResult(JsonResponse(Fixture())));
    await Expect<InvalidDataException>(() => new PocketTtsModelDiscoveryService(mismatchClient)
        .DiscoverAsync(new string('d', 40)));
});

await Verify("Missing or redacted hashes remain unavailable", async () =>
{
    JsonObject fixture = Fixture();
    fixture["siblings"]![0]!["lfs"]!["sha256"] = new string('*', 64);
    using var client = Client((_, _) => Task.FromResult(JsonResponse(fixture)));
    PocketTtsModelDiscoveryResult result = await new PocketTtsModelDiscoveryService(client).DiscoverAsync();
    Check(result.Files.Single(file => file.RepositoryPath.EndsWith("/model.safetensors")).Lfs?.Sha256 is null,
        "Redacted digest must not be used as a checksum.");
});

await Verify("Connectivity failure is explicit and has no offline fallback", async () =>
{
    var cause = new HttpRequestException("Simulated disconnected network.");
    using var client = Client((_, _) => throw cause);
    var exception = await Expect<HttpRequestException>(() => new PocketTtsModelDiscoveryService(client).DiscoverAsync());
    Check(exception.Message.Contains("Internet access is required") && ReferenceEquals(exception.InnerException, cause),
        "Connectivity failure was hidden.");
});

await Verify("HTTP access, missing-revision, rate-limit, and service errors remain explicit", async () =>
{
    foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var exception = await Expect<HttpRequestException>(() => new PocketTtsModelDiscoveryService(client).DiscoverAsync());
        Check(exception.StatusCode == status, "HTTP failure status was lost.");
    }
});

await Verify("Invalid API schemas fail instead of returning a partial catalogue", async () =>
{
    var mutations = new Action<JsonObject>[]
    {
        root => root.Remove("sha"),
        root => root["id"] = "unofficial/pocket-tts",
        root => root["sha"] = "main",
        root => root["siblings"] = new JsonObject(),
        root => root["siblings"]!.AsArray().Add(root["siblings"]![0]!.DeepClone()),
        root => root["siblings"]![0]!["rfilename"] = "../model.safetensors",
        root => root["siblings"]![0]!["size"] = -1,
        root => root["siblings"]![0]!["lfs"]!["size"] = 124,
        root => root["siblings"] = new JsonArray(),
    };
    foreach (Action<JsonObject> mutate in mutations)
    {
        JsonObject fixture = Fixture();
        mutate(fixture);
        using var client = Client((_, _) => Task.FromResult(JsonResponse(fixture)));
        await Expect<InvalidDataException>(() => new PocketTtsModelDiscoveryService(client).DiscoverAsync());
    }
});

await Verify("Malformed JSON, HTML, oversized responses, and unexpected pagination are rejected", async () =>
{
    foreach (string body in new[] { "not json", "[]" })
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Expect<InvalidDataException>(() => new PocketTtsModelDiscoveryService(client).DiscoverAsync());
    }

    using var htmlClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") }));
    await Expect<InvalidDataException>(() => new PocketTtsModelDiscoveryService(htmlClient).DiscoverAsync());
    using var oversizedClient = Client((_, _) =>
    {
        HttpResponseMessage response = JsonResponse(Fixture());
        response.Content.Headers.ContentLength = 9 * 1024 * 1024;
        return Task.FromResult(response);
    });
    await Expect<InvalidDataException>(() => new PocketTtsModelDiscoveryService(oversizedClient).DiscoverAsync());
    using var paginatedClient = Client((_, _) =>
    {
        HttpResponseMessage response = JsonResponse(Fixture());
        response.Headers.Add("Link", "<https://huggingface.co/api/models/kyutai/pocket-tts?page=2>; rel=\"next\"");
        return Task.FromResult(response);
    });
    await Expect<InvalidDataException>(() => new PocketTtsModelDiscoveryService(paginatedClient).DiscoverAsync());
});

await Verify("Caller cancellation is preserved and HTTP timeouts are distinguished", async () =>
{
    using var caller = new CancellationTokenSource();
    using var entered = new SemaphoreSlim(0);
    using var client = Client(async (_, token) =>
    {
        entered.Release();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return JsonResponse(Fixture());
    });
    Task<PocketTtsModelDiscoveryResult> operation = new PocketTtsModelDiscoveryService(client).DiscoverAsync(cancellationToken: caller.Token);
    await entered.WaitAsync(TimeSpan.FromSeconds(5));
    caller.Cancel();
    var exception = await Expect<OperationCanceledException>(async () => await operation);
    Check(exception.CancellationToken == caller.Token, "Caller cancellation token was lost.");
    using var timeoutClient = Client((_, _) => throw new TaskCanceledException("Simulated HTTP timeout."));
    await Expect<TimeoutException>(() => new PocketTtsModelDiscoveryService(timeoutClient).DiscoverAsync());
});

if (args.Contains("--live"))
{
    await Verify("Official API discovery and replay at the exact returned commit", async () =>
    {
        using var guard = new MetadataOnlyHandler { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } };
        using var client = new HttpClient(guard);
        var service = new PocketTtsModelDiscoveryService(client);
        PocketTtsModelDiscoveryResult latest = await service.DiscoverAsync();
        PocketTtsModelDiscoveryResult pinned = await service.DiscoverAsync(latest.Revision);
        Check(latest.Files.SequenceEqual(pinned.Files), "Pinned replay returned different file metadata.");
        Check(latest.Models.Select(model => model.Id).SequenceEqual(pinned.Models.Select(model => model.Id)), "Pinned replay changed candidate IDs.");
        Check(guard.RequestCount == 2 && latest.Models.Count > 0, "Unexpected API requests or no discovered models.");
        Check(latest.Models.All(model => model.RuntimeCompatibility == PocketTtsModelCompatibility.Unknown &&
            model.VoiceCloningSupport == PocketTtsVoiceCloningSupport.Unknown), "Live metadata cannot certify compatibility.");
        string root = FindRepositoryRoot();
        string evidenceDirectory = Path.Combine(root, "artifacts", "model-discovery");
        Directory.CreateDirectory(evidenceDirectory);
        string snapshotPath = Path.Combine(evidenceDirectory,
            $"discovery-{latest.Revision}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(latest, options));
        Console.WriteLine($"Repository: {latest.RepositoryId}; revision: {latest.Revision}; gating: {latest.Gating}");
        Console.WriteLine($"{latest.Models.Count} model candidates; {latest.Files.Count} file records; 0 compatibility/cloning verdicts verified.");
        foreach (PocketTtsDiscoveredModel model in latest.Models)
        {
            Console.WriteLine($"  {model.Name}: {string.Join(", ", model.CandidateWeightPaths)}");
        }
        Console.WriteLine("Metadata evidence: " + snapshotPath);
    });
}

Console.WriteLine($"{passed} targeted discovery check groups passed.");

async Task Verify(string name, Func<Task> operation)
{
    await operation();
    Console.WriteLine("PASS " + name);
    passed++;
}

static void Check(bool condition, string message)
{
    if (!condition) { throw new Exception(message); }
}

static async Task<T> Expect<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T exception) { return exception; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
    new(new StubHandler(respond));

static HttpResponseMessage JsonResponse(JsonObject root) => new(HttpStatusCode.OK)
    { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };

static JsonObject Fixture() => new()
{
    ["id"] = "kyutai/pocket-tts", ["sha"] = Commit, ["gated"] = "auto",
    ["siblings"] = new JsonArray(
        new JsonObject { ["rfilename"] = "languages/future_variant_2031/model.safetensors", ["size"] = 123, ["blobId"] = Blob,
            ["lfs"] = new JsonObject { ["sha256"] = LfsHash, ["size"] = 123, ["pointerSize"] = 128 } },
        new JsonObject { ["rfilename"] = "languages/future_variant_2031/tokenizer.json" },
        new JsonObject { ["rfilename"] = "languages/future_variant_2031/embeddings/example.safetensors", ["size"] = 12 },
        new JsonObject { ["rfilename"] = "legacy.safetensors", ["size"] = 99 })
};

static string FindRepositoryRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PocketTTSStudioLite", "PocketTTSStudioLite.csproj"))) { return directory.FullName; }
    }
    throw new DirectoryNotFoundException("Run these checks from a build inside this checkout.");
}

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
}

sealed class MetadataOnlyHandler : DelegatingHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri!.Host != "huggingface.co" ||
            !request.RequestUri.AbsolutePath.StartsWith("/api/models/kyutai/pocket-tts/revision/", StringComparison.Ordinal) ||
            request.RequestUri.Query != "?blobs=true" || request.Headers.Authorization is not null)
        {
            throw new Exception("Only anonymous official model-info metadata requests are permitted in this check.");
        }
        RequestCount++;
        return base.SendAsync(request, token);
    }
}
