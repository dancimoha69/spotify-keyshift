using System.Threading;
using System.Windows.Forms;

namespace KeyShiftBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--cleanup", StringComparison.OrdinalIgnoreCase))
        {
            InstallerService.CleanupAfterUninstall(args);
            return;
        }

        var uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        if (uninstall)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new SetupForm(uninstall: true));
            return;
        }

        var installedApp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyShiftBridge", "KeyShift.exe");
        var currentApp = Environment.ProcessPath;
        var isInstalledApp = !string.IsNullOrEmpty(currentApp) &&
            Path.GetFullPath(currentApp).Equals(Path.GetFullPath(installedApp), StringComparison.OrdinalIgnoreCase);
        if (!isInstalledApp)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new SetupForm(uninstall: false));
            return;
        }

        using var mutex = new Mutex(true, "Local\\KeyShiftBridge", out var createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        var controller = new AudioController();
        var api = new LocalApi(controller);
        var showWindow = args.Contains("--window", StringComparer.OrdinalIgnoreCase);
        using var context = new TrayContext(controller, api, showWindow);
        Application.Run(context);
    }
}
