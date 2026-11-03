using PocketTTSStudioLite.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ModelUpdateChecks
{
    private static readonly string OldRevision = new('a', 40), Revision = new('b', 40);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Model update: " + message); }
    private static byte[] Weights(string payload, bool cloning = true, int dimension = 1)
    {
        var tensors = new Dictionary<string, object>
        {
            ["flow_lm.speaker_proj_weight"] = new { dtype = "F32", shape = new[] { dimension }, data_offsets = new[] { 0, 4 } },
        };
        if (cloning) tensors["mimi.encoder.model.0.conv.weight"] = new { dtype = "F32", shape = new[] { 1 }, data_offsets = new[] { 4, 8 } };
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(tensors), tail = Encoding.UTF8.GetBytes(payload);
        byte[] result = new byte[8 + header.Length + tail.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)header.Length);
        header.CopyTo(result, 8); tail.CopyTo(result, 8 + header.Length);
        return result;
    }
    private static string Hash(byte[] bytes, bool sha)
    {
        if (sha) return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0")); hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static PocketTtsModelInstallPlan Plan(PocketTtsModelInstallPlan template, string revision, byte[] weights, byte[] tokenizer)
        => template.WithUpdatedFiles(revision, template.Files.Select(file =>
        {
            byte[] bytes = file.Asset.Weights ? weights : tokenizer;
            string hash = Hash(bytes, file.Asset.Lfs);
            return new PocketTtsModelInventoryFile(Path.GetRelativePath(PocketTtsModelStore.Root, PocketTtsModelStore.BlobPath(file.Asset.Lfs, hash)),
                file.RepositoryPath, bytes.Length, hash, file.Asset.Lfs ? "SHA256" : "GitBlobSHA1", file.Asset.Weights, PocketTtsModelInstallPlan.Repository, revision);
        }).ToArray());
    private static void Publish(PocketTtsModelInstallPlan plan, byte[] weights, byte[] tokenizer)
    {
        foreach (var file in plan.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Asset.Path)!);
            File.WriteAllBytes(file.Asset.Path, file.Asset.Weights ? weights : tokenizer);
        }
        string receipt = PocketTtsModelStore.BlobPath(true, plan.InventoryEntry().UpdateReceiptSha256!);
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!); File.WriteAllBytes(receipt, plan.UpdateReceipt!);
    }

    internal static async Task<int> RunAsync(string sandbox)
    {
        int passed = 0;
        var inventory = new PocketTtsModelInventoryService();
        byte[] tokenizer = Encoding.UTF8.GetBytes("shared tokenizer"), before = Weights("old weights"), after = Weights("new weights");
        PocketTtsModelInstallPlan old = null!, next = null!;
        async Task Fresh(byte[]? candidate = null)
        {
            PocketTtsRuntimeStore.LocalDataPath = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            PocketTtsRuntimeStore.Generation = 0;
            Directory.CreateDirectory(PocketTtsRuntimeStore.LocalDataPath);
            File.WriteAllText(PocketTtsRuntimeStore.ResolveActivePath(), "runtime fixture");
            var template = await PocketTtsModelInstallPlan.LoadAsync("english", default) ?? throw new Exception("Missing embedded model template.");
            PocketTtsModelInstallPlan.ValidateInventory(template.InventoryEntry()); // Existing 0.1.0 shape needs no receipt.
            Check(!JsonSerializer.Serialize(template.InventoryEntry()).Contains("UpdateReceiptSha256"), "Null additive metadata changed legacy identity shape.");
            old = Plan(template, OldRevision, before, tokenizer); next = Plan(template, Revision, candidate ?? after, tokenizer);
            Publish(old, before, tokenizer);
            File.WriteAllText(PocketTtsModelInventoryService.InventoryPath, JsonSerializer.Serialize(new { SchemaVersion = 2, Models = new[] { old.InventoryEntry() } }));
        }
        async Task<PocketTtsModelInstallResult> Update(Handler handler, CancellationToken token = default, IProgress<PocketTtsModelInstallProgress>? progress = null, string? bearer = null)
        {
            using var http = new HttpClient(handler);
            return await new PocketTtsModelInstallService(http, () => bearer).UpdateAsync("english",
                PocketTtsModelInventoryService.GetIdentity(old.InventoryEntry()), Revision, progress, token);
        }
        string Blob(PocketTtsModelInstallPlan plan) => plan.Files.Single(file => file.Asset.Weights).Asset.Path;
        async Task Unchanged()
        {
            Check(PocketTtsModelInventoryService.GetIdentity((await inventory.ReadAsync()).Single()) == PocketTtsModelInventoryService.GetIdentity(old.InventoryEntry()) &&
                File.Exists(Blob(old)) && (await inventory.ReadPendingAsync()).Count == 0, "Failed update changed old registration/assets.");
        }

        await Fresh();
        var handler = new Handler(next, after, tokenizer);
        string selection = Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-selection.json");
        const string preference = "{\"SchemaVersion\":1,\"SelectedAlias\":\"english\"}";
        File.WriteAllText(selection, preference);
        var result = await Update(handler);
        var registered = (await inventory.ReadAsync()).Single();
        Check(result.Succeeded && !result.CleanupPending && registered.Revision == Revision && !File.Exists(Blob(old)) && File.Exists(Blob(next)), "Successful update did not commit and clean old weights.");
        Check(!File.Exists(PocketTtsModelStore.BlobPath(true, old.InventoryEntry().UpdateReceiptSha256!)) &&
            File.Exists(PocketTtsModelStore.BlobPath(true, registered.UpdateReceiptSha256!)) && File.Exists(old.Files.Single(file => !file.Asset.Weights).Asset.Path), "Receipt/shared tokenizer lifecycle incorrect.");
        var leases = await PocketTtsModelInventoryService.CreateModel(registered).OpenVerifiedAssetsAsync(default);
        foreach (var lease in leases) lease.Dispose();
        Check((await inventory.ReadPendingAsync()).Count == 0 && !Directory.EnumerateFileSystemEntries(PocketTtsModelStore.Staging).Any(), "Completed update left cleanup records/staging.");
        Check(handler.MetadataAnonymous && handler.Downloads == 1, "Metadata obtained credentials or unchanged dependency was downloaded.");
        Check(File.ReadAllText(selection) == preference, "Update rewrote the persisted model selection.");
        passed++;

        await Fresh(); handler = new Handler(next, after, tokenizer) { RequireToken = true };
        result = await Update(handler); Check(result.Failure == PocketTtsModelInstallFailure.AuthenticationRequired, "Gated update did not request existing OAuth path."); await Unchanged();
        handler = new Handler(next, after, tokenizer) { RequireToken = true };
        result = await Update(handler, bearer: "fixture-token");
        Check(result.Succeeded && handler.MetadataAnonymous && handler.SawBearer, "Authorized retry failed or metadata leaked token.");
        passed++;

        await Fresh(); handler = new Handler(next, after, tokenizer) { RedirectDownload = true };
        result = await Update(handler, bearer: "fixture-token");
        Check(result.Succeeded && handler.SawBearer && handler.CdnAnonymous && handler.MetadataAnonymous, "Redirect forwarded credentials outside the official model endpoint.");
        passed++;

        foreach (string failure in new[] { "wrong hash", "missing cloning", "shape change", "wrong revision", "network", "runtime change", "cancel" })
        {
            byte[] candidate = failure == "missing cloning" ? Weights("invalid", cloning: false) : failure == "shape change" ? Weights("changed", dimension: 2) : after;
            await Fresh(candidate); handler = new Handler(next, candidate, tokenizer);
            using var cancellation = new CancellationTokenSource();
            if (failure == "wrong hash") handler.Bytes = Weights("bad weights");
            if (failure == "wrong revision") handler.MetadataRevision = OldRevision;
            if (failure == "network") handler.FailDownload = true;
            if (failure == "runtime change") handler.OnDownload = () => PocketTtsRuntimeStore.Generation++;
            if (failure == "cancel") handler.OnDownload = cancellation.Cancel;
            result = await Update(handler, cancellation.Token);
            Check(!result.Succeeded, "Rejected scenario activated: " + failure); await Unchanged();
            Check(!Directory.Exists(PocketTtsModelStore.Staging) || !Directory.EnumerateFileSystemEntries(PocketTtsModelStore.Staging).Any(), "Failure left recorded temporary payload: " + failure);
            passed++;
        }

        await Fresh();
        using (var lockFile = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-install.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            handler = new Handler(next, after, tokenizer); result = await Update(handler);
            Check(result.Failure == PocketTtsModelInstallFailure.Busy && handler.Requests == 0, "Active installer did not block update.");
        }
        await Unchanged(); passed++;

        await Fresh();
        handler = new Handler(next, after, tokenizer);
        using (var http = new HttpClient(handler))
        {
            result = await new PocketTtsModelInstallService(http, () => null).UpdateAsync("english", new string('f', 64), Revision);
            Check(!result.Succeeded && handler.Requests == 0, "Stale confirmation performed downloads.");
        }
        await Unchanged(); passed++;

        await Fresh();
        var concurrentlyChanged = old.InventoryEntry() with { RequiredFiles = old.InventoryEntry().RequiredFiles.Reverse().ToArray() };
        handler = new Handler(next, after, tokenizer) { OnDownload = () =>
            File.WriteAllText(PocketTtsModelInventoryService.InventoryPath, JsonSerializer.Serialize(new { SchemaVersion = 2, Models = new[] { concurrentlyChanged } })) };
        result = await Update(handler);
        Check(!result.Succeeded && PocketTtsModelInventoryService.GetIdentity((await inventory.ReadAsync()).Single()) == PocketTtsModelInventoryService.GetIdentity(concurrentlyChanged) &&
            File.Exists(Blob(old)) && (await inventory.ReadPendingAsync()).Count == 0, "Concurrent inventory change was overwritten by update commit.");
        passed++;

        await Fresh();
        Publish(next, after, tokenizer);
        var replacement = next.InventoryEntry();
        var other = old.InventoryEntry() with { Alias = "other", UpdateReceiptSha256 = null };
        File.WriteAllText(PocketTtsModelInventoryService.InventoryPath, JsonSerializer.Serialize(new { SchemaVersion = 2, Models = new[] { old.InventoryEntry(), other } }));
        using (var install = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "model-install.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var writer = PocketTtsModelStore.TryAcquireWrite())
            await inventory.CommitUpdateAsync(replacement, PocketTtsModelInventoryService.GetIdentity(old.InventoryEntry()), default);
        var recovery = await new PocketTtsModelRemovalService().RecoverAsync(default);
        Check(recovery is { CleanupPending: false } && File.Exists(Blob(old)) && File.Exists(Blob(next)), "Crash recovery deleted blobs still referenced by another model.");
        Check(!File.Exists(PocketTtsModelStore.BlobPath(true, old.InventoryEntry().UpdateReceiptSha256!)), "Old update receipt was not retired.");
        passed++;

        await Fresh(); handler = new Handler(next, after, tokenizer);
        FileStream? busyReceipt = null;
        try
        {
            var progress = new InlineProgress(value =>
            {
                if (value.Message.StartsWith("Removing superseded"))
                    busyReceipt = File.OpenRead(PocketTtsModelStore.BlobPath(true, old.InventoryEntry().UpdateReceiptSha256!));
            });
            result = await Update(handler, progress: progress);
            Check(result.Succeeded && result.CleanupPending && (await inventory.ReadAsync()).Single().Revision == Revision && (await inventory.ReadPendingAsync()).Count == 1,
                "Postcommit failure was reported as activation failure or lost durable cleanup.");
        }
        finally { busyReceipt?.Dispose(); }
        Check((await new PocketTtsModelRemovalService().RecoverAsync(default)) is { CleanupPending: false } && (await inventory.ReadPendingAsync()).Count == 0, "Postcommit cleanup retry failed.");
        passed++;

        await Fresh(); Publish(next, after, tokenizer);
        replacement = next.InventoryEntry();
        File.AppendAllText(PocketTtsModelStore.BlobPath(true, replacement.UpdateReceiptSha256!), "tamper");
        try { PocketTtsModelInventoryService.CreateModel(replacement); throw new Exception("Expected receipt verification failure."); }
        catch (InvalidDataException) { }
        await Unchanged(); passed++;

        await Fresh(); Publish(next, after, tokenizer);
        replacement = next.InventoryEntry() with { ConfigurationYaml = next.Configuration + "\nunknown_path: C:/external\n" };
        try { PocketTtsModelInventoryService.CreateModel(replacement); throw new Exception("Expected config verification failure."); }
        catch (InvalidDataException) { }
        await Unchanged(); passed++;

        Console.WriteLine($"PASS: {passed} targeted model-update transaction/receipt checks with mocked HTTP and runtime diagnostics.");
        return passed;
    }

    private sealed class InlineProgress(Action<PocketTtsModelInstallProgress> report) : IProgress<PocketTtsModelInstallProgress>
    { public void Report(PocketTtsModelInstallProgress value) => report(value); }

    private sealed class Handler(PocketTtsModelInstallPlan plan, byte[] weights, byte[] tokenizer) : HttpMessageHandler
    {
        internal byte[] Bytes = weights;
        internal string MetadataRevision = Revision;
        internal bool RequireToken, FailDownload, SawBearer, RedirectDownload;
        internal bool CdnAnonymous = true;
        internal bool MetadataAnonymous = true;
        internal int Downloads, Requests;
        internal Action? OnDownload;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                MetadataAnonymous &= request.Headers.Authorization is null;
                object[] files = plan.Files.Select(file => (object)new
                {
                    rfilename = file.RepositoryPath, size = file.Asset.Size, blobId = file.Asset.Lfs ? new string('c', 40) : file.Asset.Digest,
                    lfs = file.Asset.Lfs ? new { sha256 = file.Asset.Digest, size = file.Asset.Size } : null,
                }).ToArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = PocketTtsModelInstallPlan.Repository, sha = MetadataRevision, siblings = files })) });
            }
            Downloads++;
            if (request.RequestUri.Host != "huggingface.co")
                CdnAnonymous &= request.Headers.Authorization is null;
            if (RequireToken && request.Headers.Authorization is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            SawBearer |= request.Headers.Authorization?.Parameter == "fixture-token";
            if (RedirectDownload && request.RequestUri.Host == "huggingface.co")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://cdn-lfs.huggingface.co" + request.RequestUri.AbsolutePath);
                return Task.FromResult(response);
            }
            OnDownload?.Invoke();
            if (FailDownload) throw new HttpRequestException("fixture network failure");
            bool weight = request.RequestUri.AbsolutePath.EndsWith(plan.Files.Single(file => file.Asset.Weights).RepositoryPath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(weight ? Bytes : tokenizer) });
        }
    }
}
