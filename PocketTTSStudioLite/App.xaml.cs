using Microsoft.UI.Xaml;

namespace PocketTTSStudioLite;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "maintenance", "uninstall-plan.json")) ||
            System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "maintenance", "uninstall-state.ini")))
        {
            ShowMaintenanceMessage(IntPtr.Zero, "An uninstall is unfinished. Run this installation's uninstaller again before opening Pocket TTS Studio lite.", "Pocket TTS Studio lite", 0x10);
            Exit();
            return;
        }
        _window = new MainWindow();
        _window.Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ShowMaintenanceMessage(IntPtr owner, string message, string title, uint flags);
}
