using PocketTTSStudioLite.Services;

internal static class ModelSelectionPersistenceChecks
{
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PocketTTSStudioLite-SelectionChecks-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "model-selection.json");
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        PocketTtsModelSetupEntry Entry(string name, PocketTtsModelInstallationState state = PocketTtsModelInstallationState.InstalledVerified,
            bool? compatible = true) => new(name, state, "selection fixture")
            {
                IsRuntimeSupported = compatible,
                Model = new PocketTtsInstalledModel(name, "fixture", []),
            };
        var setup = new PocketTtsModelSetupService(new PocketTtsCliService());
        var first = Entry("first");
        var second = Entry("second");
        PocketTtsModelSetupEntry[] rows = [first, second];
        setup.ApplyCheckedModels(rows);
        // Use a new settings instance for each startup to exercise disk persistence rather than cached state.
        Task<PocketTtsModelSetupEntry?> Startup() => new PocketTtsModelSelectionSettings(path).GetStartupModelAsync(setup.GetExportModels(rows));
        var settings = new PocketTtsModelSelectionSettings(path);
        try
        {
            Check(ReferenceEquals(await Startup(), first) && !File.Exists(path), "First run must retain the existing first usable model without saving an automatic selection.");
            setup.ApplyCheckedModels([]);
            Check(await Startup() is null, "First run without usable models must stay empty.");
            setup.ApplyCheckedModels(rows);
            setup.Activate(second);
            await settings.SaveAsync(second.Name);
            Check(ReferenceEquals(await Startup(), second), "Restart did not restore the explicitly chosen model.");

            first = Entry("first"); second = Entry("second"); rows = [first, second];
            setup.ApplyCheckedModels(rows);
            Check(ReferenceEquals(setup.ActiveModel, second.Model) && ReferenceEquals(await Startup(), second), "Navigation/refresh lost the selected alias or retained an old model object.");

            foreach (var unavailable in new[]
            {
                Entry("second", PocketTtsModelInstallationState.NotInstalled),
                Entry("second", PocketTtsModelInstallationState.InstalledUnverified),
                Entry("second", PocketTtsModelInstallationState.Incompatible, false),
                Entry("second", compatible: null),
                Entry("second") with { Model = null },
            })
            {
                setup.ApplyCheckedModels([first, second]); setup.Activate(second);
                rows = [first, unavailable]; setup.ApplyCheckedModels(rows);
                Check(setup.ActiveModel is null && await Startup() is null, "An unavailable selection survived reconciliation or startup fell back: " + unavailable.State);
            }
            rows = [first]; setup.ApplyCheckedModels(rows);
            Check(await Startup() is null, "A missing saved model caused fallback to another model.");
            rows = []; setup.ApplyCheckedModels(rows);
            Check(await Startup() is null, "No compatible models must leave restoration empty.");

            rows = [first, second]; setup.ApplyCheckedModels(rows); setup.Activate(second);
            await settings.ForgetRemovedModelAsync("first", setup.ActiveModel?.Name);
            setup.Deactivate("first"); rows = [second]; setup.ApplyCheckedModels(rows);
            Check(ReferenceEquals(setup.ActiveModel, second.Model) && ReferenceEquals(await Startup(), second), "Removing a different model changed the active or saved selection.");

            string? activeAlias = setup.ActiveModel?.Name;
            setup.Deactivate("second");
            await settings.ForgetRemovedModelAsync("second", activeAlias);
            rows = [first]; setup.ApplyCheckedModels(rows);
            Check(setup.ActiveModel is null && await Startup() is null, "Removing the selected model did not preserve empty selection after restart.");
            Check(!File.ReadAllText(path).Contains("second", StringComparison.Ordinal), "The removed alias remains saved.");
            rows = [first, second]; setup.ApplyCheckedModels(rows);
            Check(await Startup() is null, "Reinstalling the removed alias silently restored selection.");

            File.Delete(path); setup.Activate(first);
            await settings.ForgetRemovedModelAsync("first", setup.ActiveModel?.Name);
            setup.Deactivate("first"); rows = [second]; setup.ApplyCheckedModels(rows);
            Check(await Startup() is null, "Removing an automatically activated first-run model allowed fallback after restart.");

            await settings.SaveAsync("second");
            await settings.ForgetRemovedModelAsync("second", activeAlias: null);
            Check(await Startup() is null, "Removing an unavailable saved model retained its preference.");
            await settings.SaveAsync("second");
            Check(ReferenceEquals(await Startup(), second), "Explicit replacement selection did not replace the empty preference.");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await settings.SaveAsync("first", cancellation.Token); throw new Exception("Cancelled save succeeded."); }
                catch (OperationCanceledException) { passed++; }
            }
            Check(ReferenceEquals(await Startup(), second), "Cancelled save damaged the existing preference.");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Preference save left temporary files behind.");

            foreach (string invalid in new[] { "{", "null", "{\"SchemaVersion\":2}", "{\"SchemaVersion\":1,\"SelectedModelAlias\":\"../invalid\"}", new string('x', 4097) })
            {
                await File.WriteAllTextAsync(path, invalid);
                try { await Startup(); throw new Exception("Invalid settings permitted startup fallback."); }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException) { passed++; }
            }
            await settings.SaveAsync("second");
            Check(ReferenceEquals(await Startup(), second), "An explicit choice could not replace invalid settings.");

            File.Delete(path); Directory.CreateDirectory(path);
            try { await settings.SaveAsync("first"); throw new Exception("An unwritable preference path was accepted."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { passed++; }
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed save left temporary files behind.");
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
        Console.WriteLine($"PASS: {passed} targeted model-selection persistence checks. Isolated temporary settings and checked-entry fixtures only; no app, native runtime, or live model data.");
    }
}
