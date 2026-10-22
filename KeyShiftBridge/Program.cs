using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace KeyShiftBridge;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--cleanup", StringComparison.OrdinalIgnoreCase))
        {
            InstallerService.CleanupAfterUninstall(args);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--launch-spotify", StringComparison.OrdinalIgnoreCase))
        {
            LaunchSpotifyWithHelper(args);
            return;
        }

        var uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        if (uninstall)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new SetupForm(uninstall: true));
            return;
        }

        if (args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)))
        {
            var logPath = Path.Combine(Path.GetTempPath(), "KeyShift-install.log");
            try
            {
                await InstallerService.InstallAsync(new FileProgress(logPath));
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} ERROR {ex}\r\n");
                Environment.ExitCode = 1;
            }
            return;
        }

        if (args.Any(a => a.Equals("--refresh-launcher", StringComparison.OrdinalIgnoreCase)))
        {
            InstallerService.RefreshLauncher();
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
        using var context = new TrayContext(controller, api, showWindow: false);
        Application.Run(context);
    }

    private static void LaunchSpotifyWithHelper(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
            throw new InvalidOperationException("Не удалось найти Spotify.exe для запуска.");

        var appPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить Key Shift.exe.");
        Process.Start(new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "--background" },
        });

        Process.Start(new ProcessStartInfo(args[1]) { UseShellExecute = true });
    }

    private sealed class FileProgress(string path) : IProgress<string>
    {
        public void Report(string value) => File.AppendAllText(path, $"{DateTimeOffset.Now:O} {value}\r\n");
    }
}
