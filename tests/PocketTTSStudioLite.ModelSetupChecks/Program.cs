using PocketTTSStudioLite.Services;
using System.Security.Cryptography;
using System.Text;

if (args.Contains("--waveform-only", StringComparer.Ordinal))
{
    AudioWaveformDisplayChecks.Run();
    return;
}

if (args.Contains("--generation-only", StringComparer.Ordinal))
{
    await GenerationChecks.RunAsync();
    return;
}

if (args.Contains("--profile-export-only", StringComparer.Ordinal))
{
    await VoiceProfileExportChecks.RunAsync();
    await VoiceExportSettingsChecks.RunAsync();
    return;
}

if (args.Contains("--notifications-only", StringComparer.Ordinal))
{
    VoiceExportNotificationChecks.Run();
    return;
}

if (args.Contains("--selection-only", StringComparer.Ordinal))
{
    ModelSelectionChecks.Run();
    await ModelSelectionPersistenceChecks.RunAsync();
    return;
}

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string runtime = Path.Combine(root, "development/runtime/pocket-tts/v4.0.1/windows-x64/extracted/pocket-tts-3.3.0-x86_64-pc-windows-msvc/pocket-tts.exe");
int passed = 0;
void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }

var missing = await new PocketTtsModelSetupService(new PocketTtsCliService(), Path.Combine(root, "artifacts/missing-runtime.exe")).CheckAsync();
Check(!missing.RuntimeReady && missing.Message.Contains("missing"), "Missing runtime must block setup.");
passed++;

var setup = new PocketTtsModelSetupService(new PocketTtsCliService(runtime), runtime);
var result = await setup.CheckAsync();
Check(result.RuntimeReady && result.Models.Count == 25, "Validated runtime variants could not be checked: " + result.Message);
Check(result.Models.Any(entry => entry.Name == "english_2026-10"), "Official English alias missing.");
Check(result.Models.All(entry => !entry.Detail.Contains("metadata is incomplete") && !entry.Detail.Contains("No trusted configuration")), "Embedded official reference metadata was not loaded.");
Console.WriteLine($"Runtime and cache: {result.Models.Count} supported aliases, {result.Models.Count(entry => entry.CanActivate)} locally verified.");
foreach (var entry in result.Models.Where(entry => entry.CanActivate)) { Console.WriteLine("Verified: " + entry.Name); }
passed++;

try { setup.Activate(new("unknown", PocketTtsModelInstallationState.InstalledUnverified, "fixture")); throw new Exception("Activation accepted an unverified model."); }
catch (InvalidOperationException) { passed++; }

string temporary = Path.Combine(root, "artifacts", "model-setup-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    string weights = Path.Combine(temporary, "model.safetensors");
    byte[] header = Encoding.UTF8.GetBytes("{\"mimi.encoder.model.0.conv.weight\":{},\"flow_lm.speaker_proj_weight\":{}}");
    using (var output = File.Create(weights))
    {
        byte[] length = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)header.Length);
        output.Write(length); output.Write(header);
    }
    var asset = new PocketTtsCachedAsset("hf://fixture/repo/model@revision", weights, new FileInfo(weights).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(weights))), true, true);
    var model = new PocketTtsInstalledModel("fixture", "weights_path: " + asset.Reference + "\nweights_path_without_voice_cloning: hf://unwanted/fallback\n", [asset]);
    var leases = await model.OpenVerifiedAssetsAsync(default);
    foreach (var lease in leases) { lease.Dispose(); }
    string config = model.LocalConfiguration();
    Check(!config.Contains("hf://") && config.Contains(weights) && !config.Contains("without_voice_cloning"), "Offline config retained remote/fallback references.");
    passed++;
    await File.AppendAllTextAsync(weights, "tampered");
    try { await model.OpenVerifiedAssetsAsync(default); throw new Exception("Tampered asset accepted."); }
    catch (InvalidDataException) { passed++; }
    var wrongRuntime = await new PocketTtsModelSetupService(new PocketTtsCliService(weights), weights).CheckAsync();
    Check(!wrongRuntime.RuntimeReady && wrongRuntime.Message.Contains("not the validated"), "Wrong runtime accepted.");
    passed++;
}
finally { Directory.Delete(temporary, recursive: true); }
Console.WriteLine($"PASS: {passed} targeted model-setup checks. No model downloads or exports executed.");
