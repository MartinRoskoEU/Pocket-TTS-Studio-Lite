using System.Text.Json;
using System.Text.RegularExpressions;

namespace PocketTTSStudioLite.Services;

/// <summary>Durable temporary downloads and hash-bound runtime retirement. Callers retain the installation lock through cleanup.</summary>
internal sealed class PocketTtsDownloadOperation
{
    private sealed record Record(int SchemaVersion, string OperationId, string Kind, string StoreIdentity,
        string DirectoryIdentity, string[] Files)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Hashes { get; init; }
    }
    internal sealed record Recovery(bool HadWork, long DeletedBytes, bool CleanupPending, string Message);
    private static readonly string[] RuntimeFiles = ["runtime.zip", "payload/pocket-tts.exe", "payload/LICENSE", "payload/runtime-installation.json"];
    private readonly string _journal;
    private readonly Record _record;
    internal string DirectoryPath => Path.Combine(Staging(_record.Kind), _record.OperationId);
    private PocketTtsDownloadOperation(string journal, Record record) { _journal = journal; _record = record; }

    private static string Staging(string kind) => kind switch
    {
        "model" => PocketTtsModelStore.Staging,
        "runtime" or "runtime-retirement" => Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "runtimes", ".staging"),
        _ => throw new InvalidDataException("Unsupported temporary operation kind."),
    };

    internal static Task<PocketTtsDownloadOperation> BeginRuntimeAsync(CancellationToken token) => BeginAsync("runtime", RuntimeFiles, token);
    internal static Task<PocketTtsDownloadOperation> BeginModelAsync(string[] files, CancellationToken token) => BeginAsync("model", files, token);
    internal static Task<PocketTtsDownloadOperation> BeginRuntimeRetirementAsync(Dictionary<string, string> hashes, CancellationToken token) =>
        BeginAsync("runtime-retirement", RuntimeFiles[1..], token, hashes);

    private static async Task<PocketTtsDownloadOperation> BeginAsync(string kind, string[] files, CancellationToken token,
        Dictionary<string, string>? hashes = null)
    {
        string staging = Staging(kind);
        PocketTtsModelStore.ValidatePath(staging, localData: true);
        PocketTtsRuntimeStore.EnsureDirectory(staging);
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(staging, id);
        if (Path.Exists(directory)) throw new IOException("Temporary operation already exists.");
        PocketTtsRuntimeStore.EnsureDirectory(directory);
        var record = new Record(1, id, kind, PocketTtsModelStore.DirectoryIdentity(PocketTtsRuntimeStore.LocalDataPath),
            PocketTtsModelStore.DirectoryIdentity(directory), files.ToArray()) { Hashes = hashes is null ? null : new(hashes, StringComparer.Ordinal) };
        string journal = directory + ".json";
        var operation = new PocketTtsDownloadOperation(journal, record);
        operation.Validate();
        // No payload may be created until this flushed record has been published.
        // A complete .tmp record is recoverable; a torn one is preserved without adoption.
        string temporary = directory + ".tmp";
        await PocketTtsRuntimeStore.WriteJsonAsync(temporary, record, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        File.Move(temporary, journal);
        return operation;
    }

    private void Validate()
    {
        PocketTtsModelStore.ValidatePath(_journal, localData: true);
        if (_record.SchemaVersion != 1 || !Regex.IsMatch(_record.OperationId ?? "", @"\A[a-f0-9]{32}\z") ||
            !Regex.IsMatch(_record.StoreIdentity ?? "", @"\A[a-f0-9]{40}\z") ||
            !Regex.IsMatch(_record.DirectoryIdentity ?? "", @"\A[a-f0-9]{40}\z") ||
            _record.StoreIdentity != PocketTtsModelStore.DirectoryIdentity(PocketTtsRuntimeStore.LocalDataPath) ||
            Path.GetFileNameWithoutExtension(_journal) != _record.OperationId ||
            !string.Equals(Path.GetDirectoryName(_journal), Staging(_record.Kind), StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(_journal) is not (".json" or ".tmp") ||
            _record.Files is not { Length: > 0 and <= 256 } ||
            _record.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _record.Files.Length)
            throw new InvalidDataException("The temporary operation record could not be verified. Its files were preserved.");
        if (_record.Kind is "runtime" or "runtime-retirement")
        {
            string[] expected = _record.Kind == "runtime" ? RuntimeFiles : RuntimeFiles[1..];
            if (!_record.Files.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Unknown runtime staging layout was preserved.");
        }
        else if (_record.Files.Any(file => !Regex.IsMatch(file ?? "", @"\A[a-f0-9]{32}\.partial\z")))
            throw new InvalidDataException("Unknown model staging layout was preserved.");
        if (_record.Kind == "runtime-retirement")
        {
            if (_record.Hashes is null || !_record.Hashes.Keys.Order(StringComparer.Ordinal).SequenceEqual(RuntimeFiles[1..].Order(StringComparer.Ordinal)) ||
                _record.Hashes.Values.Any(hash => !Regex.IsMatch(hash ?? "", @"\A[a-fA-F0-9]{64}\z")))
                throw new InvalidDataException("Retired runtime hashes could not be verified.");
        }
        else if (_record.Hashes is not null) throw new InvalidDataException("Unexpected temporary download hash metadata.");
        PocketTtsModelStore.ValidatePath(DirectoryPath, localData: true);
        if (Directory.Exists(DirectoryPath) && PocketTtsModelStore.DirectoryIdentity(DirectoryPath) != _record.DirectoryIdentity)
            throw new InvalidDataException("The temporary operation directory changed. It was preserved.");
        if (File.Exists(DirectoryPath)) throw new InvalidDataException("The temporary directory was replaced by a file.");
    }

    private static async Task<PocketTtsDownloadOperation> ReadAsync(string journal, string kind, CancellationToken token)
    {
        PocketTtsModelStore.ValidatePath(journal, localData: true);
        using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
        PocketTtsModelStore.ValidateFile(journal, stream.SafeFileHandle, localData: true);
        var record = await ReadRecordAsync(stream, token).ConfigureAwait(false);
        if (record.Kind != kind && !(kind == "runtime" && record.Kind == "runtime-retirement"))
            throw new InvalidDataException("The temporary operation belongs to a different store.");
        var operation = new PocketTtsDownloadOperation(journal, record);
        operation.Validate();
        return operation;
    }

    private static async Task<Record> ReadRecordAsync(FileStream stream, CancellationToken token)
    {
        if (stream.Length is <= 0 or > 65536) throw new InvalidDataException("Invalid temporary operation record size.");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        string[] keys = ["SchemaVersion", "OperationId", "Kind", "StoreIdentity", "DirectoryIdentity", "Files"];
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Hashes", out var hashes))
        {
            keys = [.. keys, "Hashes"];
            if (hashes.ValueKind == JsonValueKind.Object && hashes.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != hashes.EnumerateObject().Count())
                throw new InvalidDataException("Duplicate retired runtime hash metadata was preserved.");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Unknown temporary operation metadata was preserved.");
        return document.RootElement.Deserialize<Record>() ?? throw new InvalidDataException("Invalid temporary operation record.");
    }

    internal async Task<long> CleanupAsync()
    {
        // Hold the journal against modification throughout payload validation/deletion.
        string journalIdentity;
        long deleted = 0;
        using (var journal = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            journalIdentity = PocketTtsModelStore.FileIdentity(_journal, journal.SafeFileHandle);
            var saved = await ReadRecordAsync(journal, CancellationToken.None).ConfigureAwait(false);
            if ((saved with { Files = _record.Files, Hashes = _record.Hashes }) != _record || saved.Files is null || !saved.Files.SequenceEqual(_record.Files) ||
                !JsonSerializer.SerializeToUtf8Bytes(saved.Hashes).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(_record.Hashes)))
                throw new InvalidDataException("The temporary operation record changed. Its files were preserved.");
            Validate();
            var allowed = _record.Files.Select(file => Path.Combine(DirectoryPath, file.Replace('/', Path.DirectorySeparatorChar)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var leases = new List<FileStream>();
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    directories.Add(DirectoryPath, _record.DirectoryIdentity);
                    var pending = new Stack<string>(); pending.Push(DirectoryPath);
                    while (pending.TryPop(out string? current))
                    {
                        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                        {
                            PocketTtsModelStore.ValidatePath(entry, localData: true);
                            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                            {
                                if (_record.Kind is not ("runtime" or "runtime-retirement") || entry != Path.Combine(DirectoryPath, "payload"))
                                    throw new InvalidDataException("An unrecorded temporary directory was preserved.");
                                directories.Add(entry, PocketTtsModelStore.DirectoryIdentity(entry));
                                pending.Push(entry);
                            }
                            else
                            {
                                if (!allowed.Contains(entry)) throw new InvalidDataException("An unrecorded temporary file was preserved.");
                                var file = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None);
                                leases.Add(file);
                                files.Add(entry, PocketTtsModelStore.FileIdentity(entry, file.SafeFileHandle));
                                string relative = Path.GetRelativePath(DirectoryPath, entry).Replace('\\', '/');
                                if (_record.Hashes is not null && !Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file).ConfigureAwait(false)).Equals(_record.Hashes[relative], StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("A retired runtime file changed. Its operation was preserved.");
                            }
                        }
                    }
                }
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
            // Preflight the entire layout before the first deletion. Never traverse an unknown subtree.
            foreach (var file in files)
                deleted += PocketTtsModelStore.DeleteRegularFile(file.Key, localData: true, expectedIdentity: file.Value,
                    expectedSha256: _record.Hashes?.GetValueOrDefault(Path.GetRelativePath(DirectoryPath, file.Key).Replace('\\', '/')));
            foreach (var directory in directories.OrderByDescending(pair => pair.Key.Length))
                PocketTtsModelStore.DeleteEmptyDirectory(directory.Key, directory.Value);
        }
        _ = PocketTtsModelStore.DeleteRegularFile(_journal, localData: true, expectedIdentity: journalIdentity);
        return deleted;
    }

    internal static Task<Recovery> RecoverAsync(CancellationToken token) => Task.Run(async () =>
    {
        bool work = false, pending = false;
        long deleted = 0;
        foreach (string kind in new[] { "model", "runtime" })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string staging = Staging(kind);
                PocketTtsModelStore.ValidatePath(staging, localData: true);
                bool runtimeStore = kind == "runtime" && Directory.Exists(Path.Combine(PocketTtsRuntimeStore.LocalDataPath, "runtimes"));
                if (!Directory.Exists(staging) && !runtimeStore) continue;
                string[] records = Directory.Exists(staging)
                    ? Directory.EnumerateFiles(staging).Where(path => Path.GetExtension(path) is ".json" or ".tmp").ToArray() : [];
                if (records.Length == 0 && !runtimeStore) continue;
                work |= records.Length > 0;
                using var operationLock = new FileStream(Path.Combine(PocketTtsRuntimeStore.LocalDataPath,
                    kind == "model" ? "model-install.lock" : "runtime-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                PocketTtsModelStore.ValidateFile(operationLock.Name, operationLock.SafeFileHandle, localData: true);
                foreach (string journal in records)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (records.Count(path => string.Equals(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(journal), StringComparison.OrdinalIgnoreCase)) != 1)
                            throw new InvalidDataException("Conflicting temporary records were preserved.");
                        var operation = await ReadAsync(journal, kind, token).ConfigureAwait(false);
                        if (operation._record.Kind == "runtime-retirement")
                            _ = await PocketTtsRuntimeStore.ReadVerifiedActiveAsync(token).ConfigureAwait(false);
                        deleted += await operation.CleanupAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsCleanupFailure(exception)) { pending = true; }
                }
                if (kind == "runtime")
                {
                    var inactive = await PocketTtsRuntimeStore.CleanupInactiveAsync(token).ConfigureAwait(false);
                    work |= inactive.HadWork;
                    pending |= inactive.CleanupPending;
                    deleted += inactive.DeletedBytes;
                }
            }
            catch (Exception exception) when (IsCleanupFailure(exception)) { work = pending = true; }
        }
        return new Recovery(work, deleted, pending, pending
            ? "Temporary download or old runtime cleanup is pending. Busy, unknown or unverifiable files were preserved; cleanup will be retried at startup."
            : "Recorded temporary downloads and verified inactive runtime updates were cleaned up. Installed models, the active runtime and settings were preserved.");
    }, token);

    private static bool IsCleanupFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or InvalidOperationException or ArgumentException;

    // Uninstall holds both installation locks. Return only completely validated operations;
    // an invalid/unrecorded operation does not grant ownership of any of its contents.
    internal sealed record UninstallSnapshot(IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Directories);
    internal static async Task<UninstallSnapshot> CaptureForUninstallAsync()
    {
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ownedDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string kind in new[] { "model", "runtime" })
        {
            string staging = Staging(kind);
            PocketTtsModelStore.ValidatePath(staging, localData: true);
            if (!Directory.Exists(staging)) continue;
            string[] records = Directory.EnumerateFiles(staging).Where(path => Path.GetExtension(path) is ".json" or ".tmp").ToArray();
            foreach (string journal in records)
            {
                try
                {
                    if (records.Count(path => Path.GetFileNameWithoutExtension(path) == Path.GetFileNameWithoutExtension(journal)) != 1) continue;
                    var operation = await ReadAsync(journal, kind, default).ConfigureAwait(false);
                    var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var directoryIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var allowed = operation._record.Files.Select(name => Path.Combine(operation.DirectoryPath, name.Replace('/', Path.DirectorySeparatorChar))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var directories = new Stack<string>();
                    if (Directory.Exists(operation.DirectoryPath)) { directories.Push(operation.DirectoryPath); directoryIdentities.Add(operation.DirectoryPath, operation._record.DirectoryIdentity); }
                    while (directories.TryPop(out string? directory))
                    {
                        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            PocketTtsModelStore.ValidatePath(path, localData: true);
                            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                            {
                                if (kind != "runtime" || path != Path.Combine(operation.DirectoryPath, "payload")) throw new InvalidDataException("Unrecorded temporary directory.");
                                directoryIdentities.Add(path, PocketTtsModelStore.DirectoryIdentity(path));
                                directories.Push(path); continue;
                            }
                            if (!allowed.Contains(path)) throw new InvalidDataException("Unrecorded temporary file.");
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            PocketTtsModelStore.ValidateFile(path, stream.SafeFileHandle, localData: true);
                            string hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream).ConfigureAwait(false));
                            if (operation._record.Hashes is { } hashes && !hash.Equals(hashes[Path.GetRelativePath(operation.DirectoryPath, path).Replace('\\', '/')], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Retired runtime changed.");
                            files.Add(path, hash);
                        }
                    }
                    using var record = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
                    PocketTtsModelStore.ValidateFile(journal, record.SafeFileHandle, localData: true);
                    files.Add(journal, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(record).ConfigureAwait(false)));
                    foreach (var file in files) owned.Add(file.Key, file.Value);
                    foreach (var directory in directoryIdentities) ownedDirectories.Add(directory.Key, directory.Value);
                }
                catch (Exception exception) when (IsCleanupFailure(exception)) { /* Preserve the whole unverified operation. */ }
            }
        }
        return new(owned, ownedDirectories);
    }
}
