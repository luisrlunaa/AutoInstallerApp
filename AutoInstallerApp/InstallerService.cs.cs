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
        // Enable or disable the UI automation helper (can be toggled from the UI)
        public static bool EnableUiAutomation = true;
        // Named pipe used for agent communication
        private const string AgentPipeName = "AutoInstallerAgentPipe";

        // Launch the same executable as an elevated agent (once). Returns true if agent launch was initiated.
        public static bool LaunchElevatedAgent(Action<string> logCallback)
        {
            try
            {
                // Try to determine current executable path. For single-file apps Assembly.Location may be empty;
                // prefer Process.MainModule, fall back to entry assembly location and finally AppContext.BaseDirectory.
                string? exe = null;
                try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(exe))
                {
                    try { exe = System.Reflection.Assembly.GetEntryAssembly()?.Location; } catch { }
                }
                if (string.IsNullOrEmpty(exe))
                {
                    exe = AppContext.BaseDirectory; // last-resort: base directory (may not be full exe path)
                }
                if (string.IsNullOrEmpty(exe))
                    return false;

                var psi = new ProcessStartInfo(exe, "--agent")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };

                // Start elevated agent (user will see UAC)
                Process? agent = null;
                try
                {
                    agent = Process.Start(psi);
                    if (agent == null)
                    {
                        logCallback?.Invoke("[AGENT ERROR] Failed to start elevated agent (Process.Start returned null).");
                        return false;
                    }

                    logCallback?.Invoke($"[AGENT] Launched agent process PID={agent.Id}, HasExited={agent.HasExited}");

                    // Log any recorded failure reasons so caller/UI can report them
                    try
                    {
                        if (!FailureReasons.IsEmpty)
                        {
                            logCallback($"[ERROR] {FailureReasons.Count} installation failures recorded:");
                            foreach (var kv in FailureReasons)
                            {
                                try
                                {
                                    var name = Path.GetFileName(kv.Key);
                                    logCallback($"  {name}: {kv.Value}");
                                    try { Logger.Write($"[ERROR] {name}: {kv.Value}"); } catch { }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    // Wait for agent to be ready by pinging the named pipe
                    logCallback?.Invoke("[INFO] Elevated agent started. Waiting for agent to accept pipe connections...");

                    var swatch = System.Diagnostics.Stopwatch.StartNew();
                    int timeoutMs = 30000; // increased timeout for agent readiness
                    logCallback?.Invoke($"[AGENT] Waiting up to {timeoutMs}ms for agent to respond to ping...");
                    while (swatch.ElapsedMilliseconds < timeoutMs)
                    {
                        try
                        {
                            using (var client = new System.IO.Pipes.NamedPipeClientStream(".", AgentPipeName, System.IO.Pipes.PipeDirection.InOut))
                            {
                                // try connect with small timeout
                                try { client.Connect(500); } catch { throw; }
                                using (var sr = new System.IO.StreamReader(client, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
                                using (var sw = new System.IO.StreamWriter(client, System.Text.Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true })
                                {
                                    sw.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { cmd = "ping" }));
                                    string? resp = sr.ReadLine();
                                    if (resp != null && resp.Contains("pong"))
                                    {
                                        logCallback?.Invoke("[AGENT] Agent responded to ping.");
                                        return true;
                                    }
                                }
                            }
                        }
                        catch { /* keep retrying */ }

                        Thread.Sleep(500);
                    }

                    logCallback?.Invoke("[AGENT] Agent did not respond to ping within timeout.");
                    return false;
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke("[AGENT ERROR] Failed to start elevated agent: " + ex.Message);
                    return false;
                }
            }
            catch { }

            return false;
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
                    }
                }
            }
            catch { }
        }

        public static void RecordFailure(string file, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return;
                if (string.IsNullOrEmpty(reason)) reason = "(no details)";
                FailureReasons.AddOrUpdate(file, reason, (k, v) => reason);
                try { Logger.Write($"[FAILURE] {Path.GetFileName(file)}: {reason}"); } catch { }
            }
            catch { }
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
                catch { /* ignore log read errors and continue to registry checks */ }

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

            var failed = new ConcurrentBag<string>();
            var postponed = new ConcurrentBag<string>();
            int progressCount = 0;
            int totalItems = all.Count; // now includes rdp copy actions
            if (totalItems == 0) totalItems = 1;

            int maxDegree = 3;
            try { maxDegree = Math.Max(1, Environment.ProcessorCount); } catch { }
            // limit to a sensible maximum to avoid too many parallel installers
            maxDegree = Math.Min(maxDegree, 3);

            var semaphore = new SemaphoreSlim(maxDegree);
            var tasks = new List<Task>();

            foreach (var file in all)
            {
                if (token.IsCancellationRequested)
                    break;

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (token.IsCancellationRequested)
                            return;

                        // Publish current file so UI can request skip
                        CurrentFile = file;
                        StartedOrder.Enqueue(file);

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

                        // If another installer is already active, wait a bit (instead of postponing) to avoid losing the item
                        if (IsAnotherInstallerRunning())
                        {
                            logCallback($"[CONFLICT] Another installer active - waiting briefly before running {Path.GetFileName(file)}");
                            WaitForInstallerToBeFree(logCallback, token);
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
                                failed.Add(file);
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
                            ok = await InstallAsync(file, logCallback, token).ConfigureAwait(false);
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
                                int percent = (int)Math.Round(100.0 * completedCount / totalItems);
                                try { progressCallback?.Invoke(percent); } catch { }
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, token));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch { }

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

                    // === FALLBACK AHK ===
                    if (!success && !token.IsCancellationRequested)
                    {
                        logCallback($"[INFO] FlaUI no logró automatizar {name}. Probando AutoHotkey...");

                        try
                        {
                            if (CurrentProcess == null || CurrentProcess.HasExited)
                            {
                                logCallback("[AHK] Cannot start AutoHotkey fallback: CurrentProcess is null or exited.");
                                return false;
                            }

                            string ahkScript = AutoHotkeyHelper.CreateAutoHotkeyScript(CurrentProcess.Id);
                            Process ahk = Process.Start("AutoHotkey.exe", $"\"{ahkScript}\"");

                            while (!CurrentProcess.HasExited)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    try { ahk.Kill(); } catch { }
                                    break;
                                }
                                Thread.Sleep(300);
                            }

                            try { ahk.Kill(); } catch { }
                            success = true;
                        }
                        catch (Exception ex)
                        {
                            logCallback($"[AHK ERROR] {ex.Message}");
                        }
                    }

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

                    CancellationTokenSource? automatorCtsOffice = null;
                    try
                    {
                        lock (ProcessLock) { CurrentProcess = office; }
                        office.Start();
                        ActiveProcesses.TryAdd(office.Id, office);

                        try
                        {
                            automatorCtsOffice = CancellationTokenSource.CreateLinkedTokenSource(token);
                            string? pnameOffice = null;
                            try { pnameOffice = Path.GetFileNameWithoutExtension(file); } catch { }
                            InstallerUiAutomator.InteractWithProcess(office.Id, logCallback, automatorCtsOffice.Token, processNameHint: pnameOffice);
                        }
                        catch { }

                        while (!office.HasExited)
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { office.Kill(); } catch { }
                                try { automatorCtsOffice?.Cancel(); } catch { }
                                return false;
                            }

                            Thread.Sleep(200);
                        }

                        try { automatorCtsOffice?.Cancel(); } catch { }

                        if (office.ExitCode != 0)
                        {
                            RecordFailure(file, $"Exit code {office.ExitCode}");
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
                string localFile;
                // Use pre-copied local copy if available
                if (LocalCopies.TryGetValue(file, out var mapped) && File.Exists(mapped))
                {
                    localFile = mapped;
                    logCallback?.Invoke($"[INFO] Using pre-copied local temp: {localFile}");
                }
                else
                {
                    string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                    Directory.CreateDirectory(tempFolder);

                    localFile = Path.Combine(tempFolder, Path.GetFileName(file));
                    try
                    {
                        File.Copy(file, localFile, true);
                        logCallback?.Invoke($"[INFO] Copied to local temp: {localFile}");
                    }
                    catch (Exception ex)
                    {
                        // Fallback to original file if copy fails
                        logCallback?.Invoke($"[WARN] Failed to copy to temp, using original: {ex.Message}");
                        localFile = file;
                    }
                }

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

                CancellationTokenSource? automatorCts = null;
                try
                {
                    lock (ProcessLock) { CurrentProcess = process; }
                    process.Start();
                    ActiveProcesses.TryAdd(process.Id, process);

                    try
                    {
                        automatorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        // If this process was started elevated and our process is not elevated, launching a helper
                        // may be necessary. We still start background automator here; the separate helper exists
                        // for scenarios where the app can spawn an elevated agent.
                        string? pname = null;
                        try { pname = Path.GetFileNameWithoutExtension(localFile); } catch { }
                        InstallerUiAutomator.InteractWithProcess(process.Id, logCallback, automatorCts.Token, processNameHint: pname);
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

                    if (process.ExitCode != 0)
                    {
                        RecordFailure(file, $"Exit code {process.ExitCode}");
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
                    logCallback("[INFO] Installer requires elevation. Attempting elevated agent and elevated installer...");

                    try
                    {
                        // Start elevated agent (user will accept UAC)
                        LaunchElevatedAgent(logCallback);

                        // Prepare local copy of installer as earlier
                        string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                        Directory.CreateDirectory(tempFolder);
                        string localFilePath = Path.Combine(tempFolder, Path.GetFileName(file));
                        try { File.Copy(file, localFilePath, true); } catch { localFilePath = file; }

                        // Determine arguments
                        string exeNameLocal = Path.GetFileName(localFilePath).ToLower();
                        string elevatedArgs = string.Empty;

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
                        try
                        {
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

                        // Give the agent some time to start and accept pipe connections
                        Thread.Sleep(1200);

                        // Ask elevated agent to attach and automate the elevated installer
                        var attached = SendAttachCommandToAgent(elevatedProc.Id, logCallback);
                        if (!attached)
                        {
                            logCallback("[AGENT] Could not attach to elevated installer.");
                            try { RecordFailure(file, "Agent attach failed"); } catch { }
                        }

                        // Wait for elevated installer to exit
                        while (!elevatedProc.HasExited)
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { elevatedProc.Kill(); } catch { }
                                return false;
                            }
                            Thread.Sleep(200);
                        }
                        if (elevatedProc.ExitCode != 0)
                        {
                            try { RecordFailure(file, $"Elevated exit code {elevatedProc.ExitCode}"); } catch { }
                        }

                        return elevatedProc.ExitCode == 0;
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
    }
}
