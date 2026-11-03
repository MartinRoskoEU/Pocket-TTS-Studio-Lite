using PocketTTSStudioLite.Maintenance;
using System.Diagnostics;
using System.Text;

if (args.Length != 6 || args[0] is not ("prepare" or "commit" or "uninstall") || args[5] is not ("0" or "1"))
{
    Console.Error.WriteLine("This helper is invoked only by the Pocket TTS Studio lite installer.");
    return 2;
}
try
{
    var maintenance = new InstallationMaintenance(args[1]);
    var package = await InstallationMaintenance.ReadPackageAsync(args[2], args[3]);
    if (args[0] == "uninstall")
    {
        using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
        using var registration = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + MaintenanceFiles.AppId + "}_is1");
        if (registration?.GetValue("InstallLocation") is not string registered ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)).Equals(maintenance.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The current-user installation registration does not match this directory.");
    }
    foreach (var process in Process.GetProcessesByName("PocketTTSStudioLite"))
    {
        using (process)
        {
            string? path = process.MainModule?.FileName;
            if (path is null || (args[0] == "uninstall" && args[5] == "1") || path.StartsWith(maintenance.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Close Pocket TTS Studio lite before continuing.");
        }
    }
    bool complete = true;
    if (args[0] == "prepare") await maintenance.PrepareAsync(args[3], package.Files);
    if (args[0] == "commit") complete = await maintenance.CommitAsync(package, args[3]);
    if (args[0] == "uninstall") await maintenance.UninstallAsync(args[3], args[5] == "1");
    File.WriteAllText(args[4], "[results]\r\nschema=1\r\ncomplete=" + (complete ? "1" : "0") + "\r\nlegacy_preserved=" + (maintenance.LegacyFilesPreserved ? "1" : "0") + "\r\n", Encoding.Unicode);
    return complete ? 0 : 3;
}
catch (Exception exception)
{
    string message = (exception is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException
        ? exception.Message : "Maintenance could not verify its records. Preserve recovery state and retry the matching installer.").Replace('\r', ' ').Replace('\n', ' ');
    Console.Error.WriteLine(message);
    File.WriteAllText(args[4], "[results]\r\nschema=1\r\ncomplete=0\r\nmessage=" + message + "\r\n", Encoding.Unicode);
    return 1;
}
