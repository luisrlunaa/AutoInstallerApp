using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AutoInstallerApp
{
    public static class InstallerService
    {
        // Current running or next-to-run installer file (may be set by InstallAllAsync)
        public static string? CurrentFile;
        // Current process being run for the CurrentFile (set inside RunInstaller)
        public static Process? CurrentProcess;
        // Global retry flag so InstallerUiAutomator can change behavior on second attempt
        public static bool IsRetryRun;

        // Named pipe used for agent communication
        private const string AgentPipeName = "AutoInstallerAgentPipe";
        // Silent args mapping (loaded from silentArgs.json in application folder)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SilentArgs = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool SilentArgsLoaded = false;
        private static string SilentArgsFile => Path.Combine(AppContext.BaseDirectory ?? AppContext.BaseDirectory, "silentArgs.json");

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
                        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
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

        // Launch the same executable as an elevated agent (once). Returns true if agent launch was initiated.
        public static bool LaunchElevatedAgent(Action<string> logCallback)
        {
            try
            {
                string? exe = null;
                try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(exe))
                {
                    try { exe = System.Reflection.Assembly.GetEntryAssembly()?.Location; } catch { }
                }
                if (string.IsNullOrEmpty(exe)) exe = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(exe)) return false;

                var psi = new ProcessStartInfo(exe, "--agent") { UseShellExecute = true, Verb = "runas" };
                try
                {
                    var agent = Process.Start(psi);
                    if (agent == null)
                    {
                        logCallback?.Invoke("[AGENT ERROR] Failed to start elevated agent (Process.Start returned null).");
                        return false;
                    }

                    logCallback?.Invoke($"[AGENT] Launched agent process PID={agent.Id}, HasExited={agent.HasExited}");

                    // Wait for agent to respond on the named pipe
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int timeoutMs = 30000;
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        try
                        {
                            using var client = new System.IO.Pipes.NamedPipeClientStream(".", AgentPipeName, System.IO.Pipes.PipeDirection.InOut);
                            client.Connect(500);
                            using var sr = new System.IO.StreamReader(client, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                            using var swr = new System.IO.StreamWriter(client, System.Text.Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
                            swr.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { cmd = "ping" }));
                            string? resp = sr.ReadLine();
                            if (resp != null && resp.Contains("pong"))
                            {
                                logCallback?.Invoke("[AGENT] Agent responded to ping.");
                                return true;
                            }
                        }
                        catch { }
                        Thread.Sleep(500);
                    }

                    logCallback?.Invoke("[AGENT] Agent did not respond to ping within timeout.");
                    return false;
                }
                catch (Exception ex)
                {
                    try { Logger.WriteException(ex, "LaunchElevatedAgent:start"); } catch { }
                    logCallback?.Invoke("[AGENT ERROR] Failed to start elevated agent: " + ex.Message);
                    return false;
                }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "LaunchElevatedAgent"); } catch { }
                return false;
            }
        }

        // Send attach command to agent via named pipe. Waits briefly for agent to accept.
        public static bool SendAttachCommandToAgent(int pid, Action<string> logCallback, int timeoutMs = 15000)
        {
            var swait = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (swait.ElapsedMilliseconds < timeoutMs)
                {
                    try
                    {
                        // Check whether PID exists and is alive
                        bool pidAlive = false;
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            pidAlive = !p.HasExited;
                            logCallback?.Invoke($"[AGENT] Attach attempt: PID={pid}, Alive={pidAlive}, Timestamp={DateTime.Now:O}");
                        }
                        catch (Exception pe)
                        {
                            logCallback?.Invoke($"[AGENT] Attach attempt: PID={pid} not found ({pe.Message}), Timestamp={DateTime.Now:O}");
                        }

                        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", AgentPipeName, System.IO.Pipes.PipeDirection.InOut))
                        {
                            // Try to connect; small timeout to allow retries
                            try { client.Connect(500); } catch { throw; }

                            if (!client.IsConnected)
                            {
                                logCallback?.Invoke($"[AGENT] Named pipe not connected yet (PID={pid})");
                                Thread.Sleep(300);
                                continue;
                            }

                            var sw = new System.IO.StreamWriter(client, System.Text.Encoding.UTF8) { AutoFlush = true };
                            var sr = new System.IO.StreamReader(client, System.Text.Encoding.UTF8);

                            var payload = System.Text.Json.JsonSerializer.Serialize(new { cmd = "attach", pid = pid });
                            logCallback?.Invoke($"[AGENT] Sending attach payload to agent: {payload}");
                            sw.WriteLine(payload);

                            // Read response with small timeout
                            var respTask = Task.Run(() => sr.ReadLine());
                            if (respTask.Wait(2000))
                            {
                                string? resp = respTask.Result;
                                logCallback?.Invoke($"[AGENT] Agent response: {resp}");
                                if (resp != null && resp.Contains("ok"))
                                    return true;
                            }
                            else
                            {
                                logCallback?.Invoke($"[AGENT] No response received from agent within 2s for PID={pid}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"[AGENT ERROR] Attach attempt failed: {ex.Message}");
                        try { Logger.WriteException(ex, "SendAttachCommandToAgent"); } catch { }
                    }

                    Thread.Sleep(700);
                }
            }
            finally
            {
                logCallback?.Invoke($"[AGENT] Attach attempts finished after {swait.ElapsedMilliseconds}ms for PID={pid}");
            }

            logCallback?.Invoke("[AGENT] Could not connect to elevated agent (timeout or no OK response).");
            return false;
        }

        public static readonly object ProcessLock = new object();
        // All active processes started by the installer (concurrent)
        public static ConcurrentDictionary<int, Process> ActiveProcesses = new ConcurrentDictionary<int, Process>();

        // Execution scheduling information
        public static ConcurrentQueue<string> StartedOrder = new ConcurrentQueue<string>();
        public static ConcurrentQueue<string> PostponedOrder = new ConcurrentQueue<string>();
        // Track which files have been completed (counted for progress)
        public static ConcurrentDictionary<string, bool> Completed = new ConcurrentDictionary<string, bool>();
        // Track failure reasons for installers
        public static ConcurrentDictionary<string, string> FailureReasons = new ConcurrentDictionary<string, string>();
        // Map of original file -> local temp copy (only for pre-copied selected installers)
        public static ConcurrentDictionary<string, string> LocalCopies = new ConcurrentDictionary<string, string>();

        public static void ResetSchedule()
        {
            StartedOrder = new ConcurrentQueue<string>();
            PostponedOrder = new ConcurrentQueue<string>();
            Completed = new ConcurrentDictionary<string, bool>();
            ActiveProcesses = new ConcurrentDictionary<int, Process>();
            FailureReasons = new ConcurrentDictionary<string, string>();
            LocalCopies = new ConcurrentDictionary<string, string>();
        }

        // Pre-copy only the selected installers to local temp folder. This avoids copying
        // installers that were present in the source folder but not selected by the user.
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

        // Heuristic: detect if the program corresponding to the file is already installed
        private static bool IsProgramInstalled(string file)
        {
            try
            {
                string fileName = Path.GetFileName(file) ?? string.Empty;
                string nameNoExt = Path.GetFileNameWithoutExtension(file)?.ToLower() ?? string.Empty;

                // 1) Check the installation log on disk C: first source of truth
                try
                {
                    var logPath = Logger.LogFilePath; // now points to C:\AutoInstallerApp\installer_log.txt
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        var lines = File.ReadAllLines(logPath);
                        if (lines != null && lines.Length > 0)
                        {
                            // Look for explicit success/skipped entries that reference this installer filename
                            if (lines.Any(l => l.IndexOf($"[DONE] {fileName}", StringComparison.OrdinalIgnoreCase) >= 0
                                                || l.IndexOf($"[SKIPPED - ALREADY INSTALLED] {fileName}", StringComparison.OrdinalIgnoreCase) >= 0
                                                || l.IndexOf($"[INSTALLED] {fileName}", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                return true;
                            }

                            // Also consider lines that mention the product name (without extension)
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
                catch (Exception ex) { try { Logger.WriteException(ex, "KillAllActiveProcesses"); } catch { } }
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
            // Run low/medium/rdp in parallel, but force highRisk installers to run sequentially
            var parallelItems = new List<string>(lowRisk.Count + mediumRisk.Count + rdpFiles.Count);
            parallelItems.AddRange(lowRisk);
            parallelItems.AddRange(mediumRisk);
            parallelItems.AddRange(rdpFiles);

            if ((parallelItems.Count + highRisk.Count) == 0)
            {
                logCallback("[INFO] No installers to run.");
                return;
            }

            // Debug: log processing order
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
            var postponed = new ConcurrentBag<string>();
            int progressCount = 0;
            int totalItems = parallelItems.Count + highRisk.Count; // includes rdp copy actions
            if (totalItems == 0) totalItems = 1;

            double valorPorTarea = 100.0 / totalItems;
            double acumulado = 0.0;
            var progressLock = new object();

            int maxDegree = 3;
            try { maxDegree = Math.Max(1, Environment.ProcessorCount); } catch (Exception ex) { Logger.WriteException(ex); }
            maxDegree = Math.Min(maxDegree, 3);

            var semaphore = new SemaphoreSlim(maxDegree);
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
                        logCallback($"[CONFLICT] Another installer active - waiting before running {Path.GetFileName(file)}");
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

            // After both phases, retry failures sequentially
            if (!failed.IsEmpty)
            {
                logCallback($"[INFO] {failed.Count} installers failed during initial run. Retrying sequentially...");

                foreach (var f in failed)
                {
                    if (token.IsCancellationRequested) break;

                    if (f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            string dest = Path.Combine(desktop, Path.GetFileName(f));
                            File.Copy(f, dest, true);
                            logCallback($"[RDP COPIED] {Path.GetFileName(f)}");
                        }
                        catch (Exception ex) { logCallback($"[RDP COPY ERROR] {f}: {ex.Message}"); }

                        if (Completed.TryAdd(f, true))
                        {
                            Interlocked.Increment(ref progressCount);
                            int percent;
                            lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
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
                    if (token.IsCancellationRequested) break;

                    if (p.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            string dest = Path.Combine(desktop, Path.GetFileName(p));
                            File.Copy(p, dest, true);
                            logCallback($"[RDP COPIED] {Path.GetFileName(p)}");
                        }
                        catch (Exception ex) { logCallback($"[RDP COPY ERROR] {p}: {ex.Message}"); }

                        if (Completed.TryAdd(p, true))
                        {
                            Interlocked.Increment(ref progressCount);
                            int percent;
                            lock (progressLock) { acumulado += valorPorTarea; percent = (int)Math.Round(acumulado); }
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

                // Reset retry state for this installer
                IsRetryRun = false;
                CurrentFile = file;

                string name = Path.GetFileName(file);
                string exeName = name.ToLower();
                logCallback($"[INSTALLING] {name}");

                try
                {
                    if (token.IsCancellationRequested)
                        return false;

                    bool success = ProcessInstallerWithUI(file, name, exeName, logCallback, token);

                    if (!token.IsCancellationRequested)
                        logCallback($"[DONE] {name}");

                    return success;
                }
                catch (Exception ex)
                {
                    logCallback($"[EXCEPTION] {file}: {ex.Message}");
                    try { RecordFailure(file, ex.Message); } catch { }
                    return false;
                }
            });
        }

        // Handles main run + AHK fallback + retry with IsRetryRun=true
        private static bool ProcessInstallerWithUI(string file, string displayName, string exeName, Action<string> logCallback, CancellationToken token)
        {
            // First attempt (IsRetryRun = false)
            IsRetryRun = false;
            bool success = RunInstaller(file, logCallback, elevated: false, token);

            // AHK fallback if FlaUI/automation failed and process existed
            if (!success && !token.IsCancellationRequested)
            {
                logCallback($"[INFO] First attempt failed for {displayName}. Trying AutoHotkey fallback and then a retry with alternate UI path...");

                try
                {
                    // If the process is not running (RunInstaller may have started and exited), we still attempt a retry run below.
                    // Create a retry flag file for AHK to detect alternate behavior
                    var retryFlag = Path.Combine(Path.GetTempPath(), $"auto_installer_retry_{Process.GetCurrentProcess().Id}.flag");
                    try { File.WriteAllText(retryFlag, "1"); } catch { }

                    // Attempt a second run with IsRetryRun = true (this will cause InstallerUiAutomator to pick alternate radio options)
                    IsRetryRun = true;
                    success = RunInstaller(file, logCallback, elevated: false, token);

                    try { if (File.Exists(retryFlag)) File.Delete(retryFlag); } catch { }
                }
                catch (Exception ex)
                {
                    logCallback($"[AHK/RETRY ERROR] {ex.Message}");
                    try { Logger.WriteException(ex, "ProcessInstallerWithUI:AHKRetry"); } catch { }
                }
            }

            return success;
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

            try
            {
                return installerProcesses.Any(p => Process.GetProcessesByName(p).Length > 0);
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "IsAnotherInstallerRunning"); } catch { }
                return false;
            }
        }

        // ============================
        // EJECUTAR INSTALADOR (Office + No silenciosos)
        // ============================
        private static bool RunInstaller(string file, Action<string> logCallback, bool elevated, CancellationToken token)
        {
            try
            {
                // 1. Resolve shortcut immediately so 'file' and 'exeName' refer to the actual target
                string actualFile = ResolveShortcut(file);
                string exeName = Path.GetFileName(actualFile).ToLower();

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
                    logCallback?.Invoke("[INFO] Office detected. Launching and returning immediately (fire-and-forget).");

                    Process officeProc = new Process();
                    officeProc.StartInfo.FileName = actualFile;
                    officeProc.StartInfo.UseShellExecute = true;
                    if (elevated) officeProc.StartInfo.Verb = "runas";

                    try
                    {
                        officeProc.Start();
                        _ = Task.Run(() =>
                            InstallerUiAutomator.InteractWithProcess(
                                officeProc.Id,
                                logCallback,
                                CancellationToken.None,
                                processNameHint: "office"));
                        Thread.Sleep(5000);
                        logCallback?.Invoke("[OK] Office launched in background. Continuing with list.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        try { Logger.WriteException(ex, "RunInstaller:OfficeLaunch"); } catch { }
                        return false;
                    }
                }

                // ============================
                // CASO ESPECIAL: spiceworks
                // ============================
                if (exeName.Contains("spiceworks"))
                {
                    string folder = Path.GetDirectoryName(actualFile) ?? "";
                    string skPath = Path.Combine(folder, "sitekey.txt");
                    if (File.Exists(skPath))
                    {
                        string key = File.ReadAllText(skPath).Trim();
                        Thread t = new Thread(() => System.Windows.Forms.Clipboard.SetText(key));
                        t.SetApartmentState(ApartmentState.STA);
                        t.Start();
                    }
                }

                // ============================
                // OTROS INSTALADORES
                // ============================
                string localFile;
                if (LocalCopies.TryGetValue(actualFile, out var mapped) && File.Exists(mapped))
                {
                    localFile = mapped;
                    logCallback?.Invoke($"[INFO] Using pre-copied local temp: {localFile}");
                }
                else
                {
                    string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                    Directory.CreateDirectory(tempFolder);

                    localFile = Path.Combine(tempFolder, Path.GetFileName(actualFile));
                    try
                    {
                        File.Copy(actualFile, localFile, true);
                        try { UnblockFile(localFile); } catch { }
                        logCallback?.Invoke($"[INFO] Copied to local temp: {localFile}");
                        LocalCopies.AddOrUpdate(actualFile, localFile, (k, v) => localFile);
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"[WARN] Failed to copy to temp, using original: {ex.Message}");
                        localFile = actualFile;
                    }
                }

                UnblockFile(localFile);
                Process process = new Process();

                if (localFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = localFile;

                    var silentArgsMapped = GetSilentArgsForExecutable(exeName);
                    if (!string.IsNullOrEmpty(silentArgsMapped))
                    {
                        process.StartInfo.Arguments = silentArgsMapped;
                        logCallback?.Invoke($"[INFO] Using mapped silent args for '{exeName}': {silentArgsMapped}");
                    }
                    else if (nonSilentInstallers.Any(k => exeName.Contains(k)))
                    {
                        process.StartInfo.Arguments = "";
                        logCallback?.Invoke("[INFO] Running without silent parameters (non-silent installer detected)");
                    }
                    else
                    {
                        string exeContent = string.Empty;
                        try
                        {
                            const int maxRead = 64 * 1024;
                            byte[] buffer = new byte[maxRead];
                            using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                            {
                                int read = fs.Read(buffer, 0, maxRead);
                                exeContent = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, read));
                            }
                        }
                        catch (Exception ex) { try { Logger.WriteException(ex, "RunInstaller:read_exe"); } catch { } }

                        if (!string.IsNullOrEmpty(exeContent) && exeContent.IndexOf("Inno Setup", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            process.StartInfo.Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
                            logCallback?.Invoke("[INFO] Inno Setup detected, using Inno silent args");
                        }
                        else if (!string.IsNullOrEmpty(exeContent) && (exeContent.IndexOf("Nullsoft", StringComparison.OrdinalIgnoreCase) >= 0 || exeContent.IndexOf("NSIS", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            process.StartInfo.Arguments = "/S";
                            logCallback?.Invoke("[INFO] NSIS/Nullsoft detected, using /S");
                        }
                        else if (!string.IsNullOrEmpty(exeContent) && exeContent.IndexOf("InstallShield", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            process.StartInfo.Arguments = "/s";
                            logCallback?.Invoke("[INFO] InstallShield detected, using /s");
                        }
                        else
                        {
                            process.StartInfo.Arguments = "/silent";
                            logCallback?.Invoke("[INFO] Using generic /silent argument as fallback");
                        }
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

                CancellationTokenSource? automatorCts = null;
                try
                {
                    lock (ProcessLock) { CurrentProcess = process; }

                    // Publish sitekey to temp if present
                    try
                    {
                        string folderPath = Path.GetDirectoryName(localFile) ?? Path.GetDirectoryName(actualFile) ?? string.Empty;
                        string siteKeyPath = Path.Combine(folderPath, "sitekey.txt");
                        if (!string.IsNullOrEmpty(folderPath) && File.Exists(siteKeyPath))
                        {
                            var sk = File.ReadAllText(siteKeyPath).Trim();
                            if (!string.IsNullOrEmpty(sk))
                            {
                                var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                                File.WriteAllText(tmpSk, sk);
                            }
                        }
                    }
                    catch { }

                    // Create retry flag file if IsRetryRun is true so AHK can detect alternate behavior
                    string retryFlagPath = Path.Combine(Path.GetTempPath(), $"auto_installer_retry_{Process.GetCurrentProcess().Id}.flag");
                    try
                    {
                        if (IsRetryRun)
                            File.WriteAllText(retryFlagPath, "1");
                        else if (File.Exists(retryFlagPath))
                            File.Delete(retryFlagPath);
                    }
                    catch { }

                    process.Start();
                    ActiveProcesses.TryAdd(process.Id, process);

                    try
                    {
                        automatorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        string? pname = null;
                        try
                        {
                            pname = Path.GetFileNameWithoutExtension(localFile).ToLower();
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(pname) && pname.Contains("tightvnc"))
                        {
                            pname = "TightVNC";
                        }

                        _ = Task.Run(() =>
                            InstallerUiAutomator.InteractWithProcess(
                                process.Id,
                                logCallback,
                                automatorCts.Token,
                                processNameHint: pname));
                    }
                    catch { }

                    while (!process.HasExited)
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            try { automatorCts?.Cancel(); } catch { }
                            return false;
                        }
                        Thread.Sleep(200);
                    }

                    try { automatorCts?.Cancel(); } catch { }

                    int exitCode = process.ExitCode;
                    if (exitCode == 3010)
                    {
                        logCallback?.Invoke("[INFO] Installer completed with exit code 3010 (reboot required). Treating as success.");
                    }
                    else if (exitCode != 0)
                    {
                        RecordFailure(actualFile, $"Exit code {exitCode}");
                    }

                    // Cleanup retry flag and sitekey temp
                    try
                    {
                        if (File.Exists(retryFlagPath)) File.Delete(retryFlagPath);
                        var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                        if (File.Exists(tmpSk)) File.Delete(tmpSk);
                    }
                    catch { }

                    return exitCode == 0 || exitCode == 3010;
                }
                finally
                {
                    try { ActiveProcesses.TryRemove(process.Id, out _); } catch { }
                    lock (ProcessLock) { if (CurrentProcess == process) CurrentProcess = null; }
                    try
                    {
                        var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                        if (File.Exists(tmpSk)) File.Delete(tmpSk);
                    }
                    catch { }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 740)
                {
                    logCallback("[INFO] Installer requires elevation. Attempting elevated agent and elevated installer...");

                    try
                    {
                        LaunchElevatedAgent(logCallback);

                        string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                        Directory.CreateDirectory(tempFolder);
                        string localFilePath = Path.Combine(tempFolder, Path.GetFileName(file));
                        try { File.Copy(file, localFilePath, true); } catch { localFilePath = file; }

                        string exeNameLocal = Path.GetFileName(localFilePath).ToLower();

                        string[] nonSilentInstallers =
                        {
                            "chrome", "firefox", "edge", "anydesk",
                            "teamviewer", "zoom", "acro", "reader",
                            "java", "jre", "winrar",
                            "sophos", "sophosinstall", "sophossetup"
                        };

                        var elevatedInfo = new ProcessStartInfo();
                        if (localFilePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            elevatedInfo.FileName = "msiexec.exe";
                            elevatedInfo.Arguments = $"/i \"{localFilePath}\" /qn /norestart";
                        }
                        else
                        {
                            elevatedInfo.FileName = localFilePath;
                            if (nonSilentInstallers.Any(k => exeNameLocal.Contains(k)))
                                elevatedInfo.Arguments = "";
                            else
                                elevatedInfo.Arguments = "/silent /verysilent /quiet /norestart";
                        }

                        elevatedInfo.UseShellExecute = true;
                        elevatedInfo.CreateNoWindow = true;
                        elevatedInfo.Verb = "runas";

                        Process? elevatedProc = null;
                        CancellationTokenSource? automatorCts = null;

                        try
                        {
                            // Publish sitekey for elevated run
                            try
                            {
                                string folderPathElev = Path.GetDirectoryName(localFilePath) ?? Path.GetDirectoryName(file) ?? string.Empty;
                                string siteKeyPathElev = Path.Combine(folderPathElev, "sitekey.txt");
                                if (!string.IsNullOrEmpty(folderPathElev) && File.Exists(siteKeyPathElev))
                                {
                                    try
                                    {
                                        var sk = File.ReadAllText(siteKeyPathElev).Trim();
                                        if (!string.IsNullOrEmpty(sk))
                                        {
                                            var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                                            File.WriteAllText(tmpSk, sk);
                                            try { logCallback?.Invoke($"[INFO] Wrote sitekey temp for elevated installer: {tmpSk}"); } catch { }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }

                            // Create retry flag if needed
                            string retryFlagPath = Path.Combine(Path.GetTempPath(), $"auto_installer_retry_{Process.GetCurrentProcess().Id}.flag");
                            try
                            {
                                if (IsRetryRun)
                                    File.WriteAllText(retryFlagPath, "1");
                                else if (File.Exists(retryFlagPath))
                                    File.Delete(retryFlagPath);
                            }
                            catch { }

                            elevatedProc = Process.Start(elevatedInfo);
                        }
                        catch (Exception startEx)
                        {
                            logCallback($"[AGENT] Failed to start elevated installer: {startEx.Message}");
                            try { RecordFailure(file, "Failed to start elevated installer: " + startEx.Message); } catch { }
                            return false;
                        }

                        if (elevatedProc == null)
                        {
                            logCallback("[AGENT] Elevated installer did not start.");
                            return false;
                        }

                        ActiveProcesses.TryAdd(elevatedProc.Id, elevatedProc);
                        lock (ProcessLock) { CurrentProcess = elevatedProc; }

                        Thread.Sleep(1200);

                        var attached = SendAttachCommandToAgent(elevatedProc.Id, logCallback);
                        if (!attached)
                        {
                            logCallback("[AGENT] Could not attach to elevated installer.");
                            try { RecordFailure(file, "Agent attach failed"); } catch { }
                        }

                        try
                        {
                            automatorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            string? pname = null;
                            try
                            {
                                pname = Path.GetFileNameWithoutExtension(localFilePath).ToLower();
                            }
                            catch { }

                            if (!string.IsNullOrEmpty(pname) && pname.Contains("tightvnc"))
                            {
                                pname = "TightVNC";
                            }

                            _ = Task.Run(() =>
                                InstallerUiAutomator.InteractWithProcess(
                                    elevatedProc.Id,
                                    logCallback,
                                    automatorCts.Token,
                                    processNameHint: pname));
                        }
                        catch { }

                        while (!elevatedProc.HasExited)
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { elevatedProc.Kill(); } catch { }
                                try { automatorCts?.Cancel(); } catch { }
                                return false;
                            }
                            Thread.Sleep(200);
                        }

                        try { automatorCts?.Cancel(); } catch { }

                        int exitCode = elevatedProc.ExitCode;
                        if (exitCode == 3010)
                        {
                            logCallback?.Invoke("[INFO] Elevated installer completed with exit code 3010 (reboot required). Treating as success.");
                        }
                        else if (exitCode != 0)
                        {
                            try { RecordFailure(file, $"Elevated exit code {exitCode}"); } catch { }
                        }

                        try
                        {
                            var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                            if (File.Exists(tmpSk))
                            {
                                try { File.Delete(tmpSk); } catch { }
                            }
                        }
                        catch { }

                        // Cleanup retry flag
                        try
                        {
                            string retryFlagPath = Path.Combine(Path.GetTempPath(), $"auto_installer_retry_{Process.GetCurrentProcess().Id}.flag");
                            if (File.Exists(retryFlagPath)) File.Delete(retryFlagPath);
                        }
                        catch { }

                        return exitCode == 0 || exitCode == 3010;
                    }
                    catch (Exception innerEx)
                    {
                        logCallback($"[AGENT ERROR] {innerEx.Message}");
                        return false;
                    }
                }

                throw;
            }
        }

        private static string ResolveShortcut(string filePath)
        {
            if (!filePath.EndsWith(".lnk", System.StringComparison.OrdinalIgnoreCase))
                return filePath;

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                var shortcut = shell.CreateShortcut(filePath);
                return shortcut.TargetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Shortcut resolution failed: " + ex.Message);
                return filePath;
            }
        }
    }
}
