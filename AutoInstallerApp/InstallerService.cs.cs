using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;

namespace AutoInstallerApp
{
    public static class InstallerService
    {
        // Current running or next-to-run installer file (may be set by InstallAllAsync)
        public static string? CurrentFile;
        // Current process being run for the CurrentFile (set inside RunInstaller)
        public static Process? CurrentProcess;
        public static readonly object ProcessLock = new object();
        // All active processes started by the installer (concurrent)
        public static ConcurrentDictionary<int, Process> ActiveProcesses = new ConcurrentDictionary<int, Process>();

        // Execution scheduling information
        public static ConcurrentQueue<string> StartedOrder = new ConcurrentQueue<string>();
        public static ConcurrentQueue<string> PostponedOrder = new ConcurrentQueue<string>();
        // Track which files have been completed (counted for progress)
        public static ConcurrentDictionary<string, bool> Completed = new ConcurrentDictionary<string, bool>();

        public static void ResetSchedule()
        {
            StartedOrder = new ConcurrentQueue<string>();
            PostponedOrder = new ConcurrentQueue<string>();
            Completed = new ConcurrentDictionary<string, bool>();
            ActiveProcesses = new ConcurrentDictionary<int, Process>();
        }

        // Heuristic: detect if the program corresponding to the file is already installed
        private static bool IsProgramInstalled(string file)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(file).ToLower();

                // Check common uninstall registry keys for a matching display name
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
                                if (!string.IsNullOrEmpty(displayName) && displayName.ToLower().Contains(name))
                                    return true;
                            }
                            catch { }
                        }
                    }
                }

                // Also check current user uninstall area
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
                                if (!string.IsNullOrEmpty(displayName) && displayName.ToLower().Contains(name))
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

        // Kill and remove all active processes
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
                catch { }
                finally
                {
                    ActiveProcesses.TryRemove(kv.Key, out _);
                }
            }
        }


        public enum RiskLevel
        {
            LowRisk,
            MediumRisk,
            HighRisk
        }

        // ============================
        // CLASIFICACIÓN AUTOMÁTICA
        // ============================
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
                    content = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch
            {
                // If we can't read the file quickly, assume medium risk
                return RiskLevel.MediumRisk;
            }

            if (content.Contains("Inno Setup") ||
                content.Contains("Nullsoft") ||
                content.Contains("InstallShield"))
                return RiskLevel.LowRisk;

            return RiskLevel.MediumRisk;
        }

        // ============================
        // INSTALACIÓN PRINCIPAL
        // ============================
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
            // Merge all installers so we attempt to start them all in parallel.
            var all = new List<string>(lowRisk.Count + mediumRisk.Count + highRisk.Count + rdpFiles.Count);
            all.AddRange(lowRisk);
            all.AddRange(mediumRisk);
            all.AddRange(highRisk);
            // Include the rdp copy steps as items to process so progress counts them
            all.AddRange(rdpFiles);

            if (all.Count == 0)
            {
                logCallback("[INFO] No installers to run.");
                return;
            }

            logCallback("[INFO] Starting ALL installers in parallel (conflicts will be postponed)...");

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 3,
                CancellationToken = token
            };

            var failed = new ConcurrentBag<string>();
            var postponed = new ConcurrentBag<string>();
            int progressCount = 0;
            int totalItems = all.Count; // now includes rdp copy actions
            if (totalItems == 0) totalItems = 1;

            await Task.Run(() =>
            {
                Parallel.ForEach(all, parallelOptions, file =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    // Publish current file so UI can request skip
                    CurrentFile = file;
                    StartedOrder.Enqueue(file);
                    // Record start order
                    // (progress is counted when an installer finishes)
                    // If program appears already installed, skip it
                    try
                    {
                        if (IsProgramInstalled(file))
                        {
                            var name = Path.GetFileName(file);
                            logCallback($"[SKIPPED - ALREADY INSTALLED] {name}");
                            if (Completed.TryAdd(file, true))
                            {
                                var completedCount = Interlocked.Increment(ref progressCount);
                                int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                                try { progressCallback?.Invoke(percent); } catch { }
                            }
                            return;
                        }
                    }
                    catch { }

                    // If user requested skip for this file: skipping feature removed — continue normal flow

                    // If another installer is already active, postpone this one instead of waiting
                    if (IsAnotherInstallerRunning())
                    {
                        postponed.Add(file);
                        PostponedOrder.Enqueue(file);
                        logCallback($"[CONFLICT] Another installer active - postponing {Path.GetFileName(file)}");
                        // conflict/postpone — no progress change now (if RDP, copy later when processed)
                        return;
                    }

                    // Handle RDP copy items directly (do not try to run them as installers)
                    if (file.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            string dest = Path.Combine(desktop, Path.GetFileName(file));
                            File.Copy(file, dest, true);
                            logCallback($"[RDP COPIED] {Path.GetFileName(file)}");
                        }
                        catch (Exception ex)
                        {
                            logCallback($"[RDP COPY ERROR] {file}: {ex.Message}");
                        }

                        if (Completed.TryAdd(file, true))
                        {
                            var completedCount = Interlocked.Increment(ref progressCount);
                            int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                            try { progressCallback?.Invoke(percent); } catch { }
                        }

                        return;
                    }

                    bool ok = false;
                    try
                    {
                        ok = InstallAsync(file, logCallback, token).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        ok = false;
                    }

                    if (!ok)
                        failed.Add(file);
                    else
                    {
                        // mark completed only once and update progress as percentage
                        if (Completed.TryAdd(file, true))
                        {
                            var completedCount = Interlocked.Increment(ref progressCount);
                            // compute percentage = 100 * completedCount / totalItems
                            int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                            try { progressCallback?.Invoke(percent); } catch { }
                        }
                    }
                });
            });

            if (!failed.IsEmpty)
            {
                logCallback($"[INFO] {failed.Count} installers failed during parallel run. Retrying sequentially...");

                foreach (var f in failed)
                {
                    if (token.IsCancellationRequested)
                        break;


                    // Sequential retry: InstallAsync will attempt elevation if needed
                    if (f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            string dest = Path.Combine(desktop, Path.GetFileName(f));
                            File.Copy(f, dest, true);
                            logCallback($"[RDP COPIED] {Path.GetFileName(f)}");
                        }
                        catch (Exception ex)
                        {
                            logCallback($"[RDP COPY ERROR] {f}: {ex.Message}");
                        }

                        if (Completed.TryAdd(f, true))
                        {
                            var completedCount = Interlocked.Increment(ref progressCount);
                            int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                            try { progressCallback?.Invoke(percent); } catch { }
                        }
                    }
                    else
                    {
                        await InstallAsync(f, logCallback, token);
                        if (Completed.TryAdd(f, true))
                        {
                            var completedCount = Interlocked.Increment(ref progressCount);
                            int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                            try { progressCallback?.Invoke(percent); } catch { }
                        }
                    }
                }
            }

            // After retries, run postponed files sequentially
            if (!postponed.IsEmpty)
            {
                logCallback($"[INFO] Running {postponed.Count} postponed installers sequentially...");

                foreach (var p in postponed)
                {
                    if (token.IsCancellationRequested)
                        break;

                    if (p.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            string dest = Path.Combine(desktop, Path.GetFileName(p));
                            File.Copy(p, dest, true);
                            logCallback($"[RDP COPIED] {Path.GetFileName(p)}");
                        }
                        catch (Exception ex)
                        {
                            logCallback($"[RDP COPY ERROR] {p}: {ex.Message}");
                        }

                        if (Completed.TryAdd(p, true))
                        {
                            var completedCount = Interlocked.Increment(ref progressCount);
                            int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                            try { progressCallback?.Invoke(percent); } catch { }
                        }
                    }
                    else
                    {
                        await InstallAsync(p, logCallback, token);
                        if (Completed.TryAdd(p, true))
                        {
                            var completedCount = Interlocked.Increment(ref progressCount);
                            int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                            try { progressCallback?.Invoke(percent); } catch { }
                        }
                    }
                }
            }
        }

        // ============================
        // INSTALAR UN SOLO ARCHIVO
        // ============================
        public static async Task<bool> InstallAsync(string file, Action<string> logCallback, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                if (token.IsCancellationRequested)
                    return false;

                string name = Path.GetFileName(file);
                logCallback($"[INSTALLING] {name}");

                try
                {
                    // Do not wait here to avoid serializing installers. Conflict detection is handled by the caller
                    if (token.IsCancellationRequested)
                        return false;

                    bool success = RunInstaller(file, logCallback, elevated: false, token);

                    if (!success && !token.IsCancellationRequested)
                    {
                        logCallback($"[INFO] Retrying with administrator privileges: {name}");

                        if (!token.IsCancellationRequested)
                        {
                            bool elevatedSuccess = RunInstaller(file, logCallback, elevated: true, token);
                            success = elevatedSuccess || success;
                        }
                    }

                    if (!token.IsCancellationRequested)
                        logCallback($"[DONE] {name}");

                    return success;
                }
                catch (Exception ex)
                {
                    logCallback($"[EXCEPTION] {file}: {ex.Message}");
                    return false;
                }
            });
        }

        // ============================
        // ESPERA INTELIGENTE
        // ============================
        private static void WaitForInstallerToBeFree(Action<string> logCallback, CancellationToken token)
        {
            int waitTime = 500;
            int maxStepWait = 15000;
            int maxTotalWait = 60000;
            int waited = 0;

            while (IsAnotherInstallerRunning())
            {
                if (token.IsCancellationRequested)
                    return;

                if (waited >= maxTotalWait)
                {
                    logCallback("[WARN] Max wait reached. Continuing anyway.");
                    break;
                }

                logCallback($"[WAIT] Another installer is running. Waiting {waitTime}ms...");
                Thread.Sleep(waitTime);

                waited += waitTime;
                waitTime = Math.Min(waitTime + 500, maxStepWait);
            }
        }

        private static bool IsAnotherInstallerRunning()
        {
            string[] installerProcesses =
            {
                "msiexec",
                // "OfficeClickToRun",  ← REMOVIDO (OfficeClickToRun SIEMPRE está activo)
                "GoogleUpdate",
                "setup",
                "installer",
                "SophosInstall",
                "TSPrint",
                "TSScan"
            };

            return installerProcesses.Any(p => Process.GetProcessesByName(p).Length > 0);
        }

        // ============================
        // EJECUTAR INSTALADOR (Office + No silenciosos)
        // ============================
        private static bool RunInstaller(string file, Action<string> logCallback, bool elevated, CancellationToken token)
        {
            try
            {
                string exeName = Path.GetFileName(file).ToLower();

                // Instaladores que NO aceptan parámetros silenciosos
                string[] nonSilentInstallers =
                {
                    "chrome", "firefox", "edge", "anydesk",
                    "teamviewer", "zoom", "acro", "reader",
                    "java", "jre", "winrar",
                    // Sophos installer does not accept generic /silent switches
                    "sophos", "sophosinstall", "sophossetup"
                };

                // ============================
                // CASO ESPECIAL: OFFICE
                // ============================
                if (exeName.Contains("office"))
                {
                    logCallback("[INFO] Office installer detected → running from original location without parameters");

                    Process office = new Process();
                    office.StartInfo.FileName = file;
                    office.StartInfo.Arguments = ""; // Office NO acepta /silent
                    office.StartInfo.UseShellExecute = true;
                    office.StartInfo.CreateNoWindow = false;

                    if (elevated)
                        office.StartInfo.Verb = "runas";

                    try
                    {
                        lock (ProcessLock) { CurrentProcess = office; }
                        office.Start();
                        ActiveProcesses.TryAdd(office.Id, office);

                        while (!office.HasExited)
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { office.Kill(); } catch { }
                                return false;
                            }


                            Thread.Sleep(200);
                        }

                        return office.ExitCode == 0;
                    }
                    finally
                    {
                        try { ActiveProcesses.TryRemove(office.Id, out _); } catch { }
                        lock (ProcessLock) { if (CurrentProcess == office) CurrentProcess = null; }
                    }
                }

                // ============================
                // OTROS INSTALADORES
                // ============================
                string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                Directory.CreateDirectory(tempFolder);

                string localFile = Path.Combine(tempFolder, Path.GetFileName(file));
                File.Copy(file, localFile, true);

                logCallback($"[INFO] Copied to local temp: {localFile}");

                Process process = new Process();

                if (localFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = localFile;

                    if (nonSilentInstallers.Any(k => exeName.Contains(k)))
                    {
                        process.StartInfo.Arguments = "";
                        logCallback("[INFO] Running without silent parameters (non-silent installer detected)");
                    }
                    else
                    {
                        process.StartInfo.Arguments = "/silent /verysilent /quiet /norestart";
                    }
                }
                else if (localFile.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = "msiexec.exe";
                    process.StartInfo.Arguments = $"/i \"{localFile}\" /qn /norestart";
                }

                process.StartInfo.UseShellExecute = true;
                process.StartInfo.CreateNoWindow = true;

                if (elevated)
                    process.StartInfo.Verb = "runas";

                try
                {
                    lock (ProcessLock) { CurrentProcess = process; }
                    process.Start();
                    ActiveProcesses.TryAdd(process.Id, process);

                    while (!process.HasExited)
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            return false;
                        }

                        Thread.Sleep(200);
                    }

                    return process.ExitCode == 0;
                }
                finally
                {
                    try { ActiveProcesses.TryRemove(process.Id, out _); } catch { }
                    lock (ProcessLock) { if (CurrentProcess == process) CurrentProcess = null; }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 740)
                {
                    logCallback("[INFO] Installer requires elevation.");
                    return false;
                }

                throw;
            }
        }
    }
}
