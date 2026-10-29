using PocketTTSStudioLite.Services;

internal static class ModelSelectionChecks
{
    internal static void Run()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            passed++;
        }
        PocketTtsModelSetupEntry Entry(string name, PocketTtsModelInstallationState state = PocketTtsModelInstallationState.InstalledVerified,
            bool? compatible = true) => new(name, state, "checked fixture")
            {
                IsRuntimeSupported = compatible,
                Model = new PocketTtsInstalledModel(name, "fixture", Array.Empty<PocketTtsCachedAsset>()),
                InventoryIdentity = name,
            };
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { passed++; return; }
            throw new InvalidOperationException(message);
        }

        var setup = new PocketTtsModelSetupService(new PocketTtsCliService());
        var first = Entry("first");
        var second = Entry("second");
        var missing = Entry("missing", PocketTtsModelInstallationState.NotInstalled);
        var unverified = Entry("unverified", PocketTtsModelInstallationState.InstalledUnverified);
        var incompatible = Entry("incompatible", PocketTtsModelInstallationState.Incompatible, false);
        var unsupported = Entry("unsupported", compatible: false);
        var unknown = Entry("unknown", compatible: null);
        var noModel = Entry("no-model") with { Model = null };
        PocketTtsModelSetupEntry[] rows = [first, second, missing, unverified, incompatible, unsupported, unknown, noModel];

        setup.ApplyCheckedModels(rows);
        Check(setup.GetExportModels(rows).Select(entry => entry.Name).SequenceEqual(["first", "second"]),
            "Export choices must contain only verified, supported entries with current model objects.");
        Check(setup.ActiveModel is null, "A refresh must not silently choose a model.");
        foreach (var rejected in rows.Skip(2))
        { Reject(() => setup.Activate(rejected), "An unusable model was activated: " + rejected.Name); }

        setup.Activate(first);
        Check(ReferenceEquals(setup.ActiveModel, first.Model), "Selecting the first model did not activate its exact export model.");
        setup.Activate(second);
        Check(ReferenceEquals(setup.ActiveModel, second.Model), "Changing selection did not change the model captured by export.");

        var refreshedFirst = Entry("first");
        var refreshedSecond = Entry("second");
        setup.ApplyCheckedModels([refreshedFirst, refreshedSecond]);
        Check(ReferenceEquals(setup.ActiveModel, refreshedSecond.Model), "Refresh lost a still-usable selection or retained an outdated model object.");
        Check(setup.GetExportModels([first, second]).Count == 0, "Stale rows remained selectable after refresh.");
        Reject(() => setup.Activate(second), "An outdated model object was activated after a refresh.");

        foreach (var unavailable in new[]
        {
            Entry("second", PocketTtsModelInstallationState.NotInstalled),
            Entry("second", PocketTtsModelInstallationState.InstalledUnverified),
            Entry("second", PocketTtsModelInstallationState.Incompatible, false),
            Entry("second", compatible: null),
        })
        {
            setup.ApplyCheckedModels([refreshedFirst, refreshedSecond]);
            setup.Activate(refreshedSecond);
            setup.ApplyCheckedModels([refreshedFirst, unavailable]);
            Check(setup.ActiveModel is null && setup.GetExportModels([refreshedFirst, unavailable]).Count == 1,
                "An unavailable selected model was retained or replaced without user selection.");
        }

        setup.ApplyCheckedModels([refreshedFirst, refreshedSecond]);
        setup.Activate(refreshedSecond);
        setup.ApplyCheckedModels([refreshedFirst]);
        Check(setup.ActiveModel is null, "Removing the selected registration did not clear selection.");

        setup.ApplyCheckedModels([refreshedFirst, refreshedSecond]);
        setup.Activate(refreshedSecond);
        setup.ClearActiveModel();
        Check(setup.ActiveModel is null && setup.GetExportModels([refreshedFirst, refreshedSecond]).Count == 2,
            "Clearing an invalid selection discarded otherwise available choices.");
        setup.Activate(refreshedSecond);
        setup.Deactivate("second");
        Check(setup.ActiveModel is null && setup.GetExportModels([refreshedFirst, refreshedSecond]).Single().Name == "first",
            "A removed/deactivated model remained active or selectable through stale UI rows.");

        setup.ApplyCheckedModels(Array.Empty<PocketTtsModelSetupEntry>());
        Check(setup.ActiveModel is null && setup.GetExportModels(rows).Count == 0, "An empty/failed runtime check retained a selectable model.");
        Console.WriteLine($"PASS: {passed} targeted model-selection checks. In-memory checked entries only; no GUI, native runtime, model downloads, or live inventory access.");
    }
}
