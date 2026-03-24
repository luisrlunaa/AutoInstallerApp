using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AutoInstallerApp
{
    public static class InstallerService
    {
        // =========================
        // Campos públicos / estado
        // =========================

        // Current running or next-to-run installer file (may be set by InstallAllAsync)
        public static string? CurrentFile;
        // Current process being run for the CurrentFile (set inside RunInstaller)
        public static Process? CurrentProcess;
        // Global retry flag so InstallerUiAutomator can change behavior on second attempt
        public static bool IsRetryRun;

        // Named pipe used for agent communication
        private const string AgentPipeName = "AutoInstallerAgentPipe";
        // Silent args mapping (loaded from silentArgs.json in application folder)
        private static readonly ConcurrentDictionary<string, string> SilentArgs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool SilentArgsLoaded = false;
        private static string SilentArgsFile => Path.Combine(AppContext.BaseDirectory ?? AppContext.BaseDirectory, "silentArgs.json");

        // Concurrency and scheduling structures
        public static readonly object ProcessLock = new object();
        public static ConcurrentDictionary<int, Process> ActiveProcesses = new ConcurrentDictionary<int, Process>();
        public static ConcurrentQueue<string> StartedOrder = new ConcurrentQueue<string>();
        public static ConcurrentQueue<string> PostponedOrder = new ConcurrentQueue<string>();
        public static ConcurrentDictionary<string, bool> Completed = new ConcurrentDictionary<string, bool>();
        public static ConcurrentDictionary<string, string> FailureReasons = new ConcurrentDictionary<string, string>();
        public static ConcurrentDictionary<string, string> LocalCopies = new ConcurrentDictionary<string, string>();

        // =========================
        // Configuración por defecto
        // =========================

        // Número máximo de procesos paralelos (ajustado por CPU)
        private static int MaxParallel = Math.Min(Math.Max(1, Environment.ProcessorCount), 3);

        // =========================
        // Utilidades privadas
        // =========================

        private static void LoadSilentArgs()
        {
            if (SilentArgsLoaded) return;
            try
            {
                if (File.Exists(SilentArgsFile))
                {
                    try
                    {
                        var txt = File.ReadAllText(SilentArgsFile);
                        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
                        if (map != null)
                        {
                            foreach (var kv in map)
                            {
                                SilentArgs.AddOrUpdate(kv.Key, kv.Value ?? string.Empty, (k, v) => kv.Value ?? string.Empty);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Logger.WriteException(ex, "LoadSilentArgs:parse"); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "LoadSilentArgs"); } catch { }
            }
            finally { SilentArgsLoaded = true; }
        }

        private static string? GetSilentArgsForExecutable(string exeName)
        {
            try
            {
                if (!SilentArgsLoaded) LoadSilentArgs();
                foreach (var kv in SilentArgs)
                {
                    try
                    {
                        if (exeName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                            return kv.Value;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { try { Logger.WriteException(ex, "GetSilentArgsForExecutable"); } catch { } }
            return null;
        }

        private static string? FindAutoHotkeyExe()
        {
            try
            {
                var appPath = AppContext.BaseDirectory ?? string.Empty;
                var candidate = Path.Combine(appPath, "AutoHotkey.exe");
                if (File.Exists(candidate)) return candidate;

                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var p in pathEnv.Split(Path.PathSeparator))
                {
                    try
                    {
                        var f = Path.Combine(p.Trim(), "AutoHotkey.exe");
                        if (File.Exists(f)) return f;
                    }
                    catch { }
                }

                var tmp = Path.Combine(Path.GetTempPath(), "AutoHotkey.exe");
                if (File.Exists(tmp)) return tmp;
            }
            catch (Exception ex) { try { Logger.WriteException(ex, "FindAutoHotkeyExe"); } catch { } }
            return null;
        }

        // Remove Mark of the Web to avoid Open File Security Warning popups for copied files
        private static void UnblockFile(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command \"Unblock-File -Path '{path}'\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit();
            }
            catch { }
        }

        // =========================
        // Preparación de copias locales
        // =========================

        public static void ResetSchedule()
        {
            StartedOrder = new ConcurrentQueue<string>();
            PostponedOrder = new ConcurrentQueue<string>();
            Completed = new ConcurrentDictionary<string, bool>();
            ActiveProcesses = new ConcurrentDictionary<int, Process>();
            FailureReasons = new ConcurrentDictionary<string, string>();
            LocalCopies = new ConcurrentDictionary<string, string>();
        }

        public static void PrepareLocalCopies(IEnumerable<string> files, Action<string>? logCallback)
        {
            try
            {
                string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                Directory.CreateDirectory(tempFolder);

                foreach (var f in files)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(f) || !File.Exists(f))
                            continue;

                        string dest = Path.Combine(tempFolder, Path.GetFileName(f));
                        File.Copy(f, dest, true);
                        LocalCopies.AddOrUpdate(f, dest, (k, v) => dest);
                        try { logCallback?.Invoke($"[INFO] Pre-copied to local temp: {dest}"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { logCallback?.Invoke($"[WARN] Could not pre-copy {Path.GetFileName(f)}: {ex.Message}"); } catch { }
                        try { Logger.WriteException(ex, "PrepareLocalCopies"); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "PrepareLocalCopies"); } catch { }
            }
        }

        // =========================
        // Heurísticas y utilidades
        // =========================

        public static void RecordFailure(string file, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return;
                if (string.IsNullOrEmpty(reason)) reason = "(no details)";
                FailureReasons.AddOrUpdate(file, reason, (k, v) => reason);
                try { Logger.WriteException(new Exception(reason), $"[FAILURE] {Path.GetFileName(file)}"); } catch { }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "RecordFailure"); } catch { }
            }
        }

        private static bool IsProgramInstalled(string file)
        {
            try
            {
                string fileName = Path.GetFileName(file) ?? string.Empty;
                string nameNoExt = Path.GetFileNameWithoutExtension(file)?.ToLower() ?? string.Empty;

                // 1) Check the installation log on disk C: first source of truth
                try
                {
                    var logPath = Logger.LogFilePath;
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        var lines = File.ReadAllLines(logPath);
                        if (lines != null && lines.Length > 0)
                        {
                            if (lines.Any(l => l.IndexOf($"[DONE] {fileName}", StringComparison.OrdinalIgnoreCase) >= 0
                                                || l.IndexOf($"[SKIPPED - ALREADY INSTALLED] {fileName}", StringComparison.OrdinalIgnoreCase) >= 0
                                                || l.IndexOf($"[INSTALLED] {fileName}", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                return true;
                            }

                            if (lines.Any(l => l.IndexOf(nameNoExt, StringComparison.OrdinalIgnoreCase) >= 0
                                                && (l.IndexOf("[DONE]", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("[SKIPPED", StringComparison.OrdinalIgnoreCase) >= 0)))
                            {
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { Logger.WriteException(ex, "IsProgramInstalled:read_log"); } catch { }
                }

                // 2) Check common uninstall registry keys for a matching display name
                string[] roots = new[] {
                    "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                    "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
                };

                foreach (var root in roots)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null) continue;

                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var sk = key.OpenSubKey(sub);
                                var displayName = (sk?.GetValue("DisplayName") as string) ?? string.Empty;
                                if (!string.IsNullOrEmpty(displayName) && displayName.ToLower().Contains(nameNoExt))
                                    return true;
                            }
                            catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
                        }
                    }
                }

                using (var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"))
                {
                    if (key != null)
                    {
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var sk = key.OpenSubKey(sub);
                                var displayName = (sk?.GetValue("DisplayName") as string) ?? string.Empty;
                                if (!string.IsNullOrEmpty(displayName) && displayName.ToLower().Contains(nameNoExt))
                                    return true;
                            }
                            catch { }
                        }
                    }
                }

                return false;
            }
            catch { return false; }
        }

        // =========================
        // Clasificación de riesgo
        // =========================

        public enum RiskLevel
        {
            LowRisk,
            MediumRisk,
            HighRisk
        }

        public static RiskLevel GetRiskLevel(string file)
        {
            string name = Path.GetFileName(file).ToLower();

            if (file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                return RiskLevel.HighRisk;

            string[] highRiskKeywords =
            {
                "office", "sophos", "tsprint", "tsscan",
                "erad", "vnc", "tightvnc", "agent", "driver"
            };

            if (highRiskKeywords.Any(k => name.Contains(k)))
                return RiskLevel.HighRisk;

            string[] mediumRiskKeywords =
            {
                "chrome", "adobe", "reader", "acrobat",
                "java", "jre", "jre_", "jre-", "setup", "installer", "update"
            };

            if (mediumRiskKeywords.Any(k => name.Contains(k)))
                return RiskLevel.MediumRisk;

            // Read only the first part of the file to detect installer engine strings (faster for large exes)
            string content = string.Empty;
            try
            {
                const int maxRead = 64 * 1024; // 64 KB
                byte[] buffer = new byte[maxRead];

                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                {
                    int read = fs.Read(buffer, 0, maxRead);
                    content = Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch
            {
                return RiskLevel.MediumRisk;
            }

            if (content.Contains("Inno Setup") ||
                content.Contains("Nullsoft") ||
                content.Contains("InstallShield"))
                return RiskLevel.LowRisk;

            return RiskLevel.MediumRisk;
        }

        // =========================
        // Instalación principal
        // =========================

        public static async Task InstallAllAsync(
            List<string> lowRisk,
            List<string> mediumRisk,
            List<string> highRisk,
            List<string> rdpFiles,
            int originalTotalFiles,
            Action<string> logCallback,
            Action<int>? progressCallback,
            CancellationToken token)
        {
            var parallelItems = new List<string>(lowRisk.Count + mediumRisk.Count + rdpFiles.Count);
            parallelItems.AddRange(lowRisk);
            parallelItems.AddRange(mediumRisk);
            parallelItems.AddRange(rdpFiles);

            if ((parallelItems.Count + highRisk.Count) == 0)
            {
                logCallback("[INFO] No installers to run.");
                return;
            }

            try
            {
                logCallback("[DEBUG] Parallel processing items:");
                foreach (var f in parallelItems) try { logCallback("  " + f); } catch { }
                logCallback("[DEBUG] HighRisk (sequential) items:");
                foreach (var f in highRisk) try { logCallback("  " + f); } catch { }
            }
            catch { }

            logCallback("[INFO] Starting installers (parallel for low/medium, sequential for high risk)...");

            var failed = new ConcurrentBag<string>();
            int progressCount = 0;
            int totalItems = parallelItems.Count + highRisk.Count;
            if (totalItems == 0) totalItems = 1;

            double valorPorTarea = 100.0 / totalItems;
            double acumulado = 0.0;
            var progressLock = new object();

            var semaphore = new SemaphoreSlim(MaxParallel);
            var tasks = new List<Task>();

            // Parallel phase
            foreach (var file in parallelItems)
            {
                if (token.IsCancellationRequested) break;

                try { if (!SilentArgsLoaded) { try { LoadSilentArgs(); } catch { } } } catch { }

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (token.IsCancellationRequested) return;

                        CurrentFile = file;
                        StartedOrder.Enqueue(file);

                        try
                        {
                            if (IsProgramInstalled(file))
                            {
                                var name = Path.GetFileName(file);
                                logCallback($"[SKIPPED - ALREADY INSTALLED] {name}");
                                if (Completed.TryAdd(file, true))
                                {
                                    Interlocked.Increment(ref progressCount);
                                    int percent;
                                    lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
                                    try { progressCallback?.Invoke(percent); } catch { }
                                }
                                return;
                            }
                        }
                        catch { }

                        if (IsAnotherInstallerRunning())
                        {
                            logCallback($"[CONFLICT] Another installer active - waiting briefly before running {Path.GetFileName(file)}");
                            WaitForInstallerToBeFree(logCallback, token);
                        }

                        if (file.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                                string dest = Path.Combine(desktop, Path.GetFileName(file));
                                File.Copy(file, dest, true);
                                logCallback($"[RDP COPIED] {Path.GetFileName(file)}");
                            }
                            catch (Exception ex) { logCallback($"[RDP COPY ERROR] {file}: {ex.Message}"); failed.Add(file); }

                            if (Completed.TryAdd(file, true))
                            {
                                Interlocked.Increment(ref progressCount);
                                int percent;
                                lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
                                try { progressCallback?.Invoke(percent); } catch { }
                            }
                            return;
                        }

                        bool ok = false;
                        try { ok = await InstallAsync(file, logCallback, token).ConfigureAwait(false); } catch { ok = false; }

                        if (!ok) failed.Add(file);
                        else
                        {
                            if (Completed.TryAdd(file, true))
                            {
                                Interlocked.Increment(ref progressCount);
                                int percent;
                                lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
                                try { progressCallback?.Invoke(percent); } catch { }
                            }
                        }
                    }
                    finally { semaphore.Release(); }
                }, token));
            }

            try { await Task.WhenAll(tasks); } catch { }

            // Sequential highRisk phase
            if (!token.IsCancellationRequested)
            {
                foreach (var file in highRisk)
                {
                    if (token.IsCancellationRequested) break;

                    CurrentFile = file;
                    StartedOrder.Enqueue(file);

                    try
                    {
                        if (IsProgramInstalled(file))
                        {
                            var name = Path.GetFileName(file);
                            logCallback($"[SKIPPED - ALREADY INSTALLED] {name}");
                            if (Completed.TryAdd(file, true))
                            {
                                Interlocked.Increment(ref progressCount);
                                int percent;
                                lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
                                try { progressCallback?.Invoke(percent); } catch { }
                            }
                            continue;
                        }
                    }
                    catch { }

                    if (IsAnotherInstallerRunning())
                    {
                        logCallback($"[CONFLICT] Another installer active - waiting briefly before running {Path.GetFileName(file)}");
                        WaitForInstallerToBeFree(logCallback, token);
                    }

                    bool ok = false;
                    try { ok = await InstallAsync(file, logCallback, token).ConfigureAwait(false); } catch { ok = false; }

                    if (!ok) failed.Add(file);
                    else
                    {
                        if (Completed.TryAdd(file, true))
                        {
                            Interlocked.Increment(ref progressCount);
                            int percent;
                            lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
                            try { progressCallback?.Invoke(percent); } catch { }
                        }
                    }
                }
            }

            // Final progress update
            try
            {
                int finalCompleted = Completed.Count;
                if (finalCompleted >= totalItems)
                {
                    progressCallback?.Invoke(100);
                }
                else
                {
                    int percent = (int)Math.Round(100.0 * finalCompleted / Math.Max(1, totalItems));
                    progressCallback?.Invoke(percent);
                }
            }
            catch { }

            if (failed.Count > 0)
            {
                foreach (var f in failed) logCallback?.Invoke($"[FAILED] {Path.GetFileName(f)}");
            }

            logCallback?.Invoke("[INFO] InstallAllAsync finished.");
        }

        // =========================
        // Public helpers para sitekey / retry flag
        // =========================

        // Public wrapper to allow InstallerUiAutomator to call FindSiteKeyNearInstaller if needed
        public static string? FindSiteKeyNearInstallerPublic(string? installerPath)
        {
            // Reuse the logic implemented in InstallerUiAutomator (keeps single source)
            try
            {
                // If InstallerUiAutomator exposes a public method, call it; otherwise fallback to local search
                // We attempt to call via reflection to avoid tight coupling; if not available, do a simple search here.
                var t = typeof(InstallerUiAutomator);
                var mi = t.GetMethod("FindSiteKeyNearInstaller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (mi != null)
                {
                    var res = mi.Invoke(null, new object?[] { installerPath }) as string;
                    return res;
                }
            }
            catch { }

            // Fallback: simple local search (same logic as earlier)
            try
            {
                if (string.IsNullOrEmpty(installerPath)) return null;
                var dir = Path.GetDirectoryName(installerPath);
                if (dir == null) return null;

                var candidates = new List<string>
                {
                    Path.Combine(dir, "sitekey"),
                    Path.Combine(dir, "sitekey.txt")
                };

                var parent = Directory.GetParent(dir);
                if (parent != null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "sitekey"));
                    candidates.Add(Path.Combine(parent.FullName, "sitekey.txt"));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }

            return null;
        }

        // Publish sitekey to temp for AHK/automator use
        private static void PublishSiteKeyForInstaller(string installerLocalPath, Action<string>? logCallback)
        {
            try
            {
                if (string.IsNullOrEmpty(installerLocalPath)) return;

                // Prefer InstallerUiAutomator's finder if available
                string? siteKeyPath = null;
                try
                {
                    siteKeyPath = FindSiteKeyNearInstallerPublic(installerLocalPath);
                }
                catch { }

                if (siteKeyPath == null)
                {
                    logCallback?.Invoke("[SERVICE] No sitekey found near installer.");
                    return;
                }

                var tmp = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                try
                {
                    File.WriteAllText(tmp, File.ReadAllText(siteKeyPath).Trim());
                    logCallback?.Invoke($"[SERVICE] Published sitekey to {tmp}");
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[SERVICE] Failed to publish sitekey: {ex.Message}");
                    try { Logger.WriteException(ex, "PublishSiteKeyForInstaller"); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "PublishSiteKeyForInstaller:outer"); } catch { }
            }
        }

        // Create retry flag for a given PID
        private static void CreateRetryFlagForPid(int pid)
        {
            try
            {
                var retryFlag = Path.Combine(Path.GetTempPath(), $"auto_installer_retry_{pid}.flag");
                File.WriteAllText(retryFlag, "1");
            }
            catch { }
        }

        // Remove retry flag for a given PID
        private static void RemoveRetryFlagForPid(int pid)
        {
            try
            {
                var retryFlag = Path.Combine(Path.GetTempPath(), $"auto_installer_retry_{pid}.flag");
                if (File.Exists(retryFlag)) File.Delete(retryFlag);
            }
            catch { }
        }

        // =========================
        // Instalación de un solo archivo
        // =========================

        public static async Task<bool> InstallAsync(string file, Action<string> logCallback, CancellationToken token)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                logCallback?.Invoke($"[ERROR] InstallAsync: file not found: {file}");
                return false;
            }

            try
            {
                // Prepare local copy if available
                string localPath = file;
                if (LocalCopies.TryGetValue(file, out var local))
                {
                    if (!string.IsNullOrEmpty(local) && File.Exists(local)) localPath = local;
                }

                // Publish sitekey to temp so AHK/automator can use it
                try { PublishSiteKeyForInstaller(localPath, logCallback); } catch { }

                // Remove any existing retry flag for this run
                try { RemoveRetryFlagForPid(Process.GetCurrentProcess().Id); } catch { }

                // Build start info
                var psi = new ProcessStartInfo(localPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(localPath) ?? Environment.CurrentDirectory
                };

                // Apply silent args if known
                var silent = GetSilentArgsForExecutable(Path.GetFileName(localPath));
                if (!string.IsNullOrEmpty(silent))
                {
                    psi.Arguments = silent;
                    psi.UseShellExecute = true;
                }

                // Unblock file to avoid Open File Security warnings
                try { UnblockFile(localPath); } catch { }

                // Start process
                var proc = Process.Start(psi);
                if (proc == null)
                {
                    logCallback?.Invoke($"[ERROR] Failed to start installer: {localPath}");
                    return false;
                }

                CurrentProcess = proc;
                ActiveProcesses.TryAdd(proc.Id, proc);
                logCallback?.Invoke($"[RUN] Started installer PID={proc.Id}, File={Path.GetFileName(localPath)}");

                // Attach automator (non-elevated) and elevated agent if needed
                try
                {
                    // Fire-and-forget automator attach
                    var cts = new CancellationTokenSource();
                    InstallerUiAutomator.InteractWithProcess(proc.Id, logCallback, cts.Token, timeoutMs: 600000, processNameHint: Path.GetFileName(localPath));
                }
                catch { }

                // Wait for process exit or cancellation
                while (!proc.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        logCallback?.Invoke("[INSTALL] Cancel requested; killed installer process.");
                        ActiveProcesses.TryRemove(proc.Id, out _);
                        return false;
                    }
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }

                // Process exited — give a short grace period for final dialogs to close
                await Task.Delay(1200, token).ConfigureAwait(false);

                // Remove active process
                ActiveProcesses.TryRemove(proc.Id, out _);

                // If installer failed (non-zero exit code) consider retry logic
                bool success = proc.ExitCode == 0;
                if (!success)
                {
                    logCallback?.Invoke($"[INSTALL] Installer exited with code {proc.ExitCode} for {Path.GetFileName(localPath)}");
                    // Create retry flag so AHK/automator can try alternate strategies on next run
                    try { CreateRetryFlagForPid(Process.GetCurrentProcess().Id); } catch { }
                }
                else
                {
                    // Ensure retry flag removed on success
                    try { RemoveRetryFlagForPid(Process.GetCurrentProcess().Id); } catch { }
                }

                return success;
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "InstallAsync"); } catch { }
                logCallback?.Invoke($"[ERROR] InstallAsync exception: {ex.Message}");
                return false;
            }
        }

        // =========================
        // Helpers para concurrencia y detección
        // =========================

        private static bool IsAnotherInstallerRunning()
        {
            try
            {
                var procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    try
                    {
                        var name = p.ProcessName.ToLowerInvariant();
                        if (name.Contains("msiexec") || name.Contains("setup") || name.Contains("install"))
                        {
                            if (!ActiveProcesses.ContainsKey(p.Id))
                                return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static void WaitForInstallerToBeFree(Action<string> logCallback, CancellationToken token)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 30000)
                {
                    if (token.IsCancellationRequested) break;
                    if (!IsAnotherInstallerRunning()) return;
                    Thread.Sleep(800);
                }
            }
            catch { }
        }

        public static void KillAllActiveProcesses()
        {
            foreach (var kv in ActiveProcesses.ToArray())
            {
                try
                {
                    var proc = kv.Value;
                    if (proc != null && !proc.HasExited)
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
                catch (Exception ex) { try { Logger.WriteException(ex, "KillAllActiveProcesses"); } catch { } }
                finally
                {
                    ActiveProcesses.TryRemove(kv.Key, out _);
                }
            }
        }

        // =========================
        // Métodos auxiliares de depuración
        // =========================

        public static void DumpStateToLog(Action<string>? logCallback)
        {
            try
            {
                logCallback?.Invoke("[STATE] Dumping InstallerService state:");
                logCallback?.Invoke($"  ActiveProcesses: {ActiveProcesses.Count}");
                logCallback?.Invoke($"  LocalCopies: {LocalCopies.Count}");
                logCallback?.Invoke($"  Completed: {Completed.Count}");
                logCallback?.Invoke($"  FailureReasons: {FailureReasons.Count}");
            }
            catch { }
        }
    }
}
