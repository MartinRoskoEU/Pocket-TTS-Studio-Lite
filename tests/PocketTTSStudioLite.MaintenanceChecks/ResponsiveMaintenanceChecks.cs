using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class ResponsiveMaintenanceChecks
{
    internal static int Run(IntPtr module, string sandbox)
    {
        var setWindow = Marshal.GetDelegateForFunctionPointer<SetWindow>(NativeLibrary.GetExport(module, "SetMaintenanceWindow"));
        var runProcess = Marshal.GetDelegateForFunctionPointer<RunProcess>(NativeLibrary.GetExport(module, "RunMaintenanceProcess"));
        var verify = Marshal.GetDelegateForFunctionPointer<Verify>(NativeLibrary.GetExport(module, "VerifyMaintenanceFile"));
        IntPtr window = CreateWindowEx(0, "STATIC", "Maintenance fixture", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Exception("Cannot create isolated message-pump fixture.");
        int messages = 0;
        WindowProcedure callback = (handle, message, wParam, lParam) =>
        {
            if (message == 0x8001) messages++;
            return DefWindowProc(handle, message, wParam, lParam);
        };
        IntPtr previous = SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(callback));
        if (previous == IntPtr.Zero) { DestroyWindow(window); throw new Exception("Cannot attach message-pump fixture."); }
        try
        {
            setWindow(window);
            PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero);
            int code = runProcess(Environment.ProcessPath!, "--maintenance-child", AppContext.BaseDirectory);
            if (code != 7 || messages != 1) throw new Exception("Maintenance wait did not pump messages or join the child with its exact exit code.");
            string file = Path.Combine(sandbox, "responsive-hash.bin");
            File.WriteAllText(file, "isolated hash fixture");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero);
            if (!verify(file, hash) || messages != 2) throw new Exception("Native file hashing blocked window messages.");
            if (verify(file, new string('0', 64))) throw new Exception("Responsive hashing accepted changed content.");
            return 3;
        }
        finally
        {
            setWindow(IntPtr.Zero);
            SetWindowLongPtr(window, -4, previous);
            DestroyWindow(window);
            GC.KeepAlive(callback);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetWindow(IntPtr window);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int RunProcess(string executable, string parameters, string directory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Verify(string path, string hash);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint style, string className, string title, uint windowStyle,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
