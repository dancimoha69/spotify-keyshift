using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyShiftBridge;

internal sealed class SetupForm : Form
{
    private readonly bool _uninstall;
    private readonly Label _status;
    private readonly Button _action;
    private readonly Button _cancel;
    private bool _finished;

    public SetupForm(bool uninstall)
    {
        _uninstall = uninstall;
        Text = uninstall ? "Удаление Key Shift" : "Установка Key Shift";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 310);
        BackColor = Color.FromArgb(20, 20, 20);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = uninstall ? "Удалить Key Shift?" : "Установить Key Shift",
            Font = new Font("Segoe UI", 22F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(28, 24),
        };
        var description = new Label
        {
            Text = uninstall
                ? "Уберёт кнопку из Spotify, вернёт исходный файл клиента и удалит приложение."
                : "Добавит кнопку Key Shift прямо в Spotify и установит аудиообработчик. Spicetify не нужен.",
            AutoSize = false,
            Location = new Point(31, 77),
            Size = new Size(495, 54),
            ForeColor = Color.Gainsboro,
        };
        _status = new Label
        {
            Text = uninstall
                ? "Закрой Spotify перед удалением."
                : "Закрой Spotify перед установкой.",
            AutoSize = false,
            Location = new Point(31, 153),
            Size = new Size(495, 44),
            ForeColor = Color.Silver,
        };
        _action = new Button
        {
            Text = uninstall ? "Удалить" : "Установить",
            Location = new Point(321, 235),
            Size = new Size(110, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 215, 96),
            ForeColor = Color.Black,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _cancel = new Button
        {
            Text = "Отмена",
            Location = new Point(444, 235),
            Size = new Size(82, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(43, 43, 43),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        _action.Click += async (_, _) => await RunActionAsync();
        _cancel.Click += (_, _) => Close();
        Controls.AddRange([title, description, _status, _action, _cancel]);
    }

    private async Task RunActionAsync()
    {
        if (_finished) { Close(); return; }
        _action.Enabled = false;
        _cancel.Enabled = false;
        ControlBox = false;
        UseWaitCursor = true;
        var acceptProgress = false;

        try
        {
            if (Process.GetProcessesByName("Spotify").Length > 0)
            {
                _status.Text = "Сначала полностью закрой Spotify, затем запусти установку снова.";
                _action.Enabled = true;
                _cancel.Enabled = true;
                ControlBox = true;
                return;
            }

            acceptProgress = true;
            var progress = new Progress<string>(text =>
            {
                if (acceptProgress) _status.Text = text;
            });
            if (_uninstall)
            {
                await InstallerService.UninstallAsync(progress);
                acceptProgress = false;
                _finished = true;
                _status.Text = "Key Shift удалён. Можно закрыть это окно.";
                _action.Text = "Готово";
                _cancel.Text = "Закрыть";
            }
            else
            {
                await InstallerService.InstallAsync(progress);
                acceptProgress = false;
                _finished = true;
                _status.Text = "Готово. Перезапусти Spotify — кнопка Key Shift появится в панели плеера.";
                _action.Text = "Готово";
                _cancel.Text = "Закрыть";
            }

            _action.Enabled = true;
            _cancel.Enabled = true;
            ControlBox = true;
        }
        catch (Exception ex)
        {
            acceptProgress = false;
            _status.Text = ex.Message;
            MessageBox.Show(this, ex.Message, _uninstall ? "Не удалось удалить Key Shift" : "Не удалось установить Key Shift",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _action.Enabled = true;
            _cancel.Enabled = true;
            ControlBox = true;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}

internal static class InstallerService
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KeyShift";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Key Shift";
    private const string AppFolderName = "KeyShiftBridge";
    private const string BeginMarker = "<!-- KEYSHIFT-DIRECT-INTEGRATION-BEGIN -->";
    private const string EndMarker = "<!-- KEYSHIFT-DIRECT-INTEGRATION-END -->";
    private static readonly string AppPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
    private static readonly string BackupFolder = Path.Combine(AppPath, "SpotifyBackup");
    private static readonly string StatePath = Path.Combine(AppPath, "install-state.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task InstallAsync(IProgress<string> progress)
    {
        var runningSpotify = Process.GetProcessesByName("Spotify");
        foreach (var process in runningSpotify) process.Dispose();
        if (runningSpotify.Length > 0)
            throw new InvalidOperationException("Закрой Spotify перед установкой.");

        await StopRunningAppAsync();

        if (File.Exists(StatePath))
        {
            var oldState = ReadState();
            if (oldState?.TargetPath is null)
                throw new InvalidOperationException("Найдена старая версия Key Shift. Сначала удали её через Параметры Windows → Приложения.");
            if (File.Exists(oldState.TargetPath) &&
                HashFile(oldState.TargetPath).Equals(oldState.PatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Key Shift уже установлен. Кнопка должна быть в панели Spotify.");

            progress.Report("Обновляю подключение после изменения Spotify…");
            RestoreSpotifyPatch();
        }

        var target = FindSpotifyTarget();
        if (target is null)
            throw new InvalidOperationException("Не нашёл классический Spotify Desktop. Установи Spotify с сайта spotify.com и открой его хотя бы один раз.");

        Directory.CreateDirectory(AppPath);
        Directory.CreateDirectory(BackupFolder);
        progress.Report("Устанавливаю приложение…");

        var currentSetup = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить файл установщика.");
        File.Copy(currentSetup, Path.Combine(AppPath, "KeyShift.exe"), true);
        File.Copy(currentSetup, Path.Combine(AppPath, "Uninstall.exe"), true);
        ExtractResource("KeyShiftSetup.Payload.THIRD_PARTY_NOTICES.md", Path.Combine(AppPath, "THIRD_PARTY_NOTICES.md"));

        var backupPath = Path.Combine(BackupFolder, $"spotify-original-{Guid.NewGuid():N}.backup");
        File.Copy(target.Path, backupPath, overwrite: false);
        var state = new InstallState
        {
            TargetPath = target.Path,
            BackupPath = backupPath,
            IsSpaArchive = target.IsSpaArchive,
            OriginalSha256 = HashFile(target.Path),
        };

        try
        {
            progress.Report("Добавляю кнопку прямо в Spotify…");
            var script = new UTF8Encoding(false, true).GetString(AssemblyResourceBytes("KeyShiftSetup.Payload.keyshift.js"));
            PatchSpotifyTarget(target.Path, target.IsSpaArchive, script);
            state.PatchedSha256 = HashFile(target.Path);
            WriteState(state);
            RegisterUninstaller();
            RegisterStartup();
            CreateShortcuts();
        }
        catch
        {
            try
            {
                if (File.Exists(target.Path)) RestoreFromBackup(target.Path, backupPath);
            }
            catch { }
            try { File.Delete(StatePath); } catch { }
            RemoveShortcuts();
            RemoveRegistryEntries();
            throw;
        }

        progress.Report("Завершаю установку…");
        Process.Start(new ProcessStartInfo(Path.Combine(AppPath, "KeyShift.exe")) { WorkingDirectory = AppPath });
    }

    public static async Task UninstallAsync(IProgress<string> progress)
    {
        progress.Report("Выключаю обработку звука…");
        await StopRunningAppAsync();
        progress.Report("Возвращаю исходные файлы Spotify…");
        RestoreSpotifyPatch();

        RemoveShortcuts();
        RemoveRegistryEntries();

        progress.Report("Удаляю файлы приложения…");
        StartCleanupHelper();
    }

    public static void CleanupAfterUninstall(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[3], out var parentPid)) return;
        var target = Path.GetFullPath(args[1]);
        while (true)
        {
            try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(500); }
            catch (ArgumentException) { break; }
            catch (InvalidOperationException) { break; }
        }
        try { Directory.Delete(target, recursive: true); } catch { }
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self)) MoveFileEx(self, null, MoveFileDelayUntilReboot);
        }
        catch { }
    }

    private static SpotifyTarget? FindSpotifyTarget()
    {
        foreach (var root in CandidateInstallRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var extractedIndex = Path.Combine(root, "Apps", "xpui", "index.html");
            if (File.Exists(extractedIndex)) return new SpotifyTarget(extractedIndex, IsSpaArchive: false);

            var spaArchive = Path.Combine(root, "Apps", "xpui.spa");
            if (File.Exists(spaArchive)) return new SpotifyTarget(spaArchive, IsSpaArchive: true);
        }
        return null;
    }

    private static IEnumerable<string> CandidateInstallRoots()
    {
        var roots = new List<string>();
        AddRoot(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify"));
        AddRoot(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify"));
        AddRoot(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Spotify"));

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var path in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                })
                {
                    using var uninstall = baseKey.OpenSubKey(path);
                    if (uninstall is null) continue;
                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = uninstall.OpenSubKey(subKeyName);
                            var displayName = app?.GetValue("DisplayName") as string;
                            if (displayName is null || !displayName.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) continue;
                            AddRoot(roots, app?.GetValue("InstallLocation") as string);
                            var displayIcon = app?.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrWhiteSpace(displayIcon))
                                AddRoot(roots, Path.GetDirectoryName(displayIcon.Trim().Trim('"')));
                        }
                        catch { }
                    }
                }

                using var appPath = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Spotify.exe");
                var executable = appPath?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(executable)) AddRoot(roots, Path.GetDirectoryName(executable.Trim().Trim('"')));
            }
            catch { }
        }

        return roots;
    }

    private static void AddRoot(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (Directory.Exists(fullPath) && !roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) roots.Add(fullPath);
        }
        catch { }
    }

    private static void PatchSpotifyTarget(string path, bool isSpaArchive, string script)
    {
        if (script.Contains("</script", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Скрипт Key Shift содержит недопустимый HTML-тег.");

        var temporaryPath = path + $".keyshift-{Guid.NewGuid():N}.tmp";
        try
        {
            if (isSpaArchive)
            {
                File.Copy(path, temporaryPath, overwrite: false);
                UpdateSpaArchive(temporaryPath, html => InjectScript(html, script));
                File.Move(temporaryPath, path, overwrite: true);
            }
            else
            {
                var html = File.ReadAllText(path);
                var patched = InjectScript(html, script);
                File.WriteAllText(temporaryPath, patched, new UTF8Encoding(false));
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static string InjectScript(string html, string script)
    {
        if (html.Contains(BeginMarker, StringComparison.Ordinal) || html.Contains(EndMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("В интерфейсе Spotify уже найден код Key Shift.");
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0) throw new InvalidOperationException("Не удалось найти точку подключения в интерфейсе Spotify.");
        var injection = $"{BeginMarker}\n<script id=\"keyshift-direct-script\">\n{script}\n</script>\n{EndMarker}\n";
        return html.Insert(bodyEnd, injection);
    }

    private static void UpdateSpaArchive(string archivePath, Func<string, string?> transform)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Update, leaveOpen: false);
        var entry = archive.GetEntry("index.html")
            ?? throw new InvalidOperationException("В установке Spotify не найден index.html.");
        string html;
        using (var input = entry.Open())
        using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            html = reader.ReadToEnd();

        var updated = transform(html);
        if (updated is null) return;
        var lastWriteTime = entry.LastWriteTime;
        entry.Delete();
        var replacement = archive.CreateEntry("index.html", CompressionLevel.Optimal);
        replacement.LastWriteTime = lastWriteTime;
        using var output = replacement.Open();
        using var writer = new StreamWriter(output, new UTF8Encoding(false));
        writer.Write(updated);
    }

    private static void RestoreSpotifyPatch()
    {
        var state = ReadState();
        if (state is null || string.IsNullOrWhiteSpace(state.TargetPath)) return;

        if (File.Exists(state.TargetPath))
        {
            var currentHash = HashFile(state.TargetPath);
            if (currentHash.Equals(state.PatchedSha256, StringComparison.OrdinalIgnoreCase) && File.Exists(state.BackupPath))
                RestoreFromBackup(state.TargetPath, state.BackupPath);
            else
                RemoveInjectedScript(state.TargetPath, state.IsSpaArchive);
        }

        if (!string.IsNullOrWhiteSpace(state.BackupPath))
        {
            try { if (File.Exists(state.BackupPath)) File.Delete(state.BackupPath); } catch { }
        }
        try { File.Delete(StatePath); } catch { }
    }

    private static void RestoreFromBackup(string targetPath, string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Не найдена сохранённая копия Spotify.", backupPath);
        var temporaryPath = targetPath + $".keyshift-restore-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(backupPath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static void RemoveInjectedScript(string targetPath, bool isSpaArchive)
    {
        if (isSpaArchive)
        {
            if (!ArchiveContainsKeyShift(targetPath)) return;
            var temporaryPath = targetPath + $".keyshift-remove-{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(targetPath, temporaryPath, overwrite: false);
                UpdateSpaArchive(temporaryPath, RemoveInjection);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
        else
        {
            var html = File.ReadAllText(targetPath);
            var cleaned = RemoveInjection(html);
            if (cleaned is not null) WriteTextAtomically(targetPath, cleaned);
        }
    }

    private static bool ArchiveContainsKeyShift(string archivePath)
    {
        using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.GetEntry("index.html");
        if (entry is null) return false;
        using var input = entry.Open();
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var html = reader.ReadToEnd();
        return html.Contains(BeginMarker, StringComparison.Ordinal) || html.Contains(EndMarker, StringComparison.Ordinal);
    }

    private static string? RemoveInjection(string html)
    {
        var start = html.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = html.IndexOf(EndMarker, start + BeginMarker.Length, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Не получилось найти конец вставки Key Shift в Spotify.");
        return html.Remove(start, end + EndMarker.Length - start);
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var temporaryPath = path + $".keyshift-write-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task StopRunningAppAsync()
    {
        var appProcesses = Process.GetProcessesByName("KeyShift").Concat(Process.GetProcessesByName("KeyShiftBridge")).ToArray();
        if (appProcesses.Length == 0) return;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.PostAsJsonAsync("http://127.0.0.1:43817/enabled", new { enabled = false });
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            foreach (var process in appProcesses) process.Dispose();
            throw new InvalidOperationException($"Не удалось безопасно выключить обработку звука. Закрой Key Shift и повтори удаление. {ex.Message}");
        }

        foreach (var process in appProcesses)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private static void RegisterUninstaller()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось добавить Key Shift в список установленных приложений.");
        var uninstaller = Path.Combine(AppPath, "Uninstall.exe");
        key.SetValue("DisplayName", "Key Shift for Spotify");
        key.SetValue("DisplayVersion", "1.0.1");
        key.SetValue("Publisher", "Key Shift");
        key.SetValue("InstallLocation", AppPath);
        key.SetValue("DisplayIcon", Path.Combine(AppPath, "KeyShift.exe"));
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void RegisterStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(RunValueName, $"\"{Path.Combine(AppPath, "KeyShift.exe")}\" --background");
    }

    private static void CreateShortcuts()
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Key Shift");
        Directory.CreateDirectory(programs);
        CreateShortcut(Path.Combine(programs, "Key Shift.lnk"), Path.Combine(AppPath, "KeyShift.exe"), "", AppPath);
    }

    private static void RemoveShortcuts()
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Key Shift");
        try { Directory.Delete(programs, recursive: true); } catch { }
    }

    private static void CreateShortcut(string path, string target, string arguments, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Не удалось создать ярлык Key Shift.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = target;
        shortcut.Description = "Управление тональностью Spotify";
        shortcut.Save();
        if (shortcut is IDisposable disposable) disposable.Dispose();
        if (shell is IDisposable shellDisposable) shellDisposable.Dispose();
    }

    private static void RemoveRegistryEntries()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
        using (var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true))
        {
            if (uninstallKey is not null)
            {
                uninstallKey.Close();
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            }
        }
    }

    private static void StartCleanupHelper()
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось запустить очистку файлов.");
        var helper = Path.Combine(Path.GetTempPath(), $"KeyShiftCleanup-{Guid.NewGuid():N}.exe");
        File.Copy(current, helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--cleanup");
        start.ArgumentList.Add(AppPath);
        start.ArgumentList.Add("--wait-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start);
    }

    private static void ExtractResource(string name, string destination)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"В установщике отсутствует файл {name}.");
        using var output = File.Create(destination);
        stream.CopyTo(output);
    }

    private static byte[] AssemblyResourceBytes(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"В установщике отсутствует файл {name}.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static InstallState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StatePath));
        }
        catch { return null; }
    }

    private static void WriteState(InstallState state) =>
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));

    private const int MoveFileDelayUntilReboot = 0x4;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);

    private sealed record SpotifyTarget(string Path, bool IsSpaArchive);

    private sealed class InstallState
    {
        public string TargetPath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public bool IsSpaArchive { get; set; }
        public string OriginalSha256 { get; set; } = "";
        public string PatchedSha256 { get; set; } = "";
    }
}
