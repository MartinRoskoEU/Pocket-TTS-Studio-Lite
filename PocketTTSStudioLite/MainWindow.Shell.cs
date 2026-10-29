using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PocketTTSStudioLite;

public sealed partial class MainWindow
{
    private void InitializeShell()
    {
        Title = $"Pocket TTS Studio lite {Services.ApplicationInfo.Version}";
        AppTitle.Text = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarInsets();
        AppWindow.Changed += (_, _) => UpdateTitleBarInsets();
        SetInitialWindowBounds();
        UpdateTitleBarInsets();
        ShellNavigation.SelectionChanged += ShellNavigation_SelectionChanged;
        NavigateTo(ModelsNavigationItem);
    }

    private void NavigateTo(NavigationViewItem item) => ShellNavigation.SelectedItem = item;

    private async void ShellNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item) { return; }

        // Keep the existing views alive, including their controls and operation state.
        bool models = item == ModelsNavigationItem;
        bool export = item == VoiceExportNavigationItem;
        ModelSetupPanel.Visibility = models ? Visibility.Visible : Visibility.Collapsed;
        VoiceExportPageHost.Visibility = export ? Visibility.Visible : Visibility.Collapsed;
        VoiceExportPanel.Visibility = export ? Visibility.Visible : Visibility.Collapsed;
        if (models)
        {
            // An idle server holds verified model leases. Stop it before exposing model-changing controls.
            ShellNavigation.IsEnabled = false;
            bool stopped = await StopGenerationAfterFailureAsync();
            if (_closing) { return; }
            ShellNavigation.IsEnabled = true;
            if (!stopped)
            {
                NavigateTo(VoiceExportNavigationItem);
                ShowVoiceExportNotification(VoiceExportNotificationKind.Warning, "Generation cleanup incomplete", "The generation runtime could not be confirmed stopped. Restart after reviewing the retained native-operation metadata before changing models.");
            }
            RevealModelInstallationCard();
        }

        if (sender.IsLoaded && sender.DisplayMode != NavigationViewDisplayMode.Expanded) { sender.IsPaneOpen = false; }
        if (export) { await CheckModelsAsync(); }
    }

    private void RenderShellNavigation()
    {
        // Preserve the existing restriction on leaving export during a picker or export operation.
        bool canLeaveExport = !_generationCleanupBlocked && !_generating && !_closingAfterGeneration && !_exportRunning && !_pickerOpen && !_referenceLoading && !_selectingExportModel && !_closingAfterReferenceAudio && !_closingAfterExport;
        ModelsNavigationItem.IsEnabled = canLeaveExport;
    }

    private void SetInitialWindowBounds()
    {
        double scale = WindowScale();
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        RectInt32 work = display.WorkArea;
        int width = Math.Min((int)Math.Round(1180 * scale), work.Width);
        int height = Math.Min((int)Math.Round(780 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height), display);
    }

    private double WindowScale() => GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

    private void UpdateTitleBarInsets() =>
        TitleBarRightInset.Width = new GridLength(AppWindow.TitleBar.RightInset / WindowScale());

    private void AvailableModelsPanel_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 480;
        Grid.SetColumnSpan(ModelsHeaderText, narrow ? 2 : 1);
        Grid.SetRow(CheckModelsButton, narrow ? 1 : 0);
        Grid.SetColumn(CheckModelsButton, narrow ? 0 : 1);
        Grid.SetColumnSpan(CheckModelsButton, narrow ? 2 : 1);
        CheckModelsButton.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        AvailableModelsHeader.RowSpacing = narrow ? 12 : 0;
    }

    private void RuntimeRow_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 480;
        Grid.SetColumnSpan(RuntimeUpdateStatusPanel, narrow ? 2 : 1);
        Grid.SetRow(RuntimeUpdateActions, narrow ? 1 : 0);
        Grid.SetColumn(RuntimeUpdateActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(RuntimeUpdateActions, narrow ? 2 : 1);
        RuntimeUpdateActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        RuntimeUpdateRow.RowSpacing = narrow ? 12 : 0;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
