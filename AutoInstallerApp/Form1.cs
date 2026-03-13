using AutoInstallerApp.Language;
using System.Diagnostics;

namespace AutoInstallerApp
{
    public partial class Form1 : Form
    {
        private CancellationTokenSource? cancelToken;
        private bool initializationFailed = false;

        public Form1()
        {
            try
            {
                InitializeComponent();
                this.MinimumSize = new Size(608, 539);
                // Initialize language resources and apply localized texts to controls (if resources available)
                try { LanguageManager.ApplyToForm(this); } catch (Exception ex) { Logger.WriteException(ex); }

                // Listen for system locale/user preference changes to re-load language at runtime
                try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged; this.FormClosed += Form1_FormClosed; } catch (Exception ex) { Logger.WriteException(ex); }
            }
            catch (Exception ex)
            {
                try { Logger.Write("[INIT ERROR] " + ex.ToString()); } catch { }
                // Do not rethrow - mark initialization failed and return early so the app remains running
                initializationFailed = true;
                try
                {
                    MessageBox.Show("The UI failed to initialize. See the log for details. The application will continue in limited mode.", "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
                return;
            }

            // Ensure STOP hidden initially
            try { btnStop.Visible = false; } catch (Exception ex) { Logger.WriteException(ex); }

            // Ensure progress label starts at 0% and is visible in front of the progress bar
            try
            {
                progressBarlbl.Text = "0%";
                progressBarlbl.Visible = true;
                progressBarlbl.BringToFront();
            }
            catch (Exception ex) { Logger.WriteException(ex); }

            this.Load += Form1_Load;
            // Timer for elapsed time display
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1000; // 1 second
            uiTimer.Tick += UiTimer_Tick;
        }

        private System.Windows.Forms.Timer uiTimer;
        private DateTime? startTime;

        // Resolve a .lnk shortcut to its target (file or folder) using WScript.Shell
        private static string? ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;
                Type? wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null) return null;
                dynamic? shell = Activator.CreateInstance(wshType);
                if (shell == null) return null;
                dynamic lnk = shell.CreateShortcut(lnkPath);
                string target = lnk.TargetPath as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target)) return null;
                return target;
            }
            catch { return null; }
        }

        private void Form1_FormClosed(object? sender, FormClosedEventArgs e)
        {
            try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged; } catch (Exception ex) { Logger.WriteException(ex); }
        }

        private void SystemEvents_UserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            try
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.Locale || e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                {
                    // Reinitialize language resources and reapply UI texts on the UI thread
                    try { this.Invoke(() => { LanguageManager.ApplyToForm(this); }); } catch (Exception ex) { Logger.WriteException(ex); }
                }
            }
            catch { }
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (startTime == null)
            {
                lblTimer.Text = "00:00:00";
                return;
            }

            var span = DateTime.Now - startTime.Value;
            lblTimer.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds);
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            // Load image lazily so startup doesn't block on large resource decoding
            try
            {
                // Load image in background to avoid UI thread pause
                Task.Run(() =>
                {
                    try
                    {
                        var img = Properties.Resources.AIA;
                        pictureBox1.Invoke(() => pictureBox1.Image = img);
                    }
                    catch (Exception ex)
                    {
                        try { Logger.Write("[LOAD IMAGE ERROR] " + ex.ToString()); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                try { Logger.Write("[LOAD IMAGE ERROR] " + ex.ToString()); } catch { }
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            string folder = txtFolder.Text;

            if (!Directory.Exists(folder))
            {
                var UNCpath = string.Empty;
                if (folder.Contains(":"))
                {
                    var newPath = folder.Split(":");
                    UNCpath = newPath.Length > 1 ? @"\\CORP.local\\PROCO" + newPath[1] : folder;
                }

                if (!Directory.Exists(UNCpath))
                {
                    MessageBox.Show("The folder does not exist.");
                    return;
                }

                txtFolder.Text = UNCpath;
            }

            // Collect installers from root and immediate child directories only (no deeper recursion)
            var installersList = new List<string>();
            try
            {
                // files directly in root
                installersList.AddRange(Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)));

                // files in immediate child directories of root (one level deep only)
                try
                {
                    var childDirs = Directory.GetDirectories(folder);
                    foreach (var d in childDirs)
                    {
                        try
                        {
                            var childFiles = Directory.GetFiles(d, "*.*", SearchOption.TopDirectoryOnly)
                                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                            f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                            f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase));
                            installersList.AddRange(childFiles);
                        }
                        catch (Exception ex) { Logger.WriteException(ex); }
                    }

                    // Also inspect .lnk shortcuts in root and immediate child directories. If a .lnk points
                    // to a folder, scan that folder for installers; if it points to a file, add the file.
                    try
                    {
                        var lnkFiles = Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly).ToList();
                        foreach (var d in childDirs)
                        {
                            try { lnkFiles.AddRange(Directory.GetFiles(d, "*.lnk", SearchOption.TopDirectoryOnly)); } catch { }
                        }

                        foreach (var lnk in lnkFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var target = ResolveShortcutTarget(lnk);
                                if (string.IsNullOrEmpty(target)) continue;

                                if (Directory.Exists(target))
                                {
                                    try
                                    {
                                        var targetFiles = Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly)
                                            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase));
                                        installersList.AddRange(targetFiles);
                                    }
                                    catch { }
                                }
                                else if (File.Exists(target))
                                {
                                    var ext = Path.GetExtension(target) ?? string.Empty;
                                    if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rdp", StringComparison.OrdinalIgnoreCase))
                                    {
                                        installersList.Add(target);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex) { Logger.WriteException(ex); }
                }
                catch (Exception ex) { Logger.WriteException(ex); }
            }
            catch (Exception ex)
            {
                try { Logger.Write("[WARN] Could not enumerate installers: " + ex.Message); } catch { }
            }

            string[] installers = installersList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            if (installers.Length == 0)
            {
                MessageBox.Show("No installers found.");
                return;
            }

            // Log discovered installers for debugging
            try
            {
                AddLog("[DEBUG] Discovered installers:");
                foreach (var it in installers)
                {
                    AddLog("  " + it);
                }
            }
            catch { }

            // Determine selected installers: either all (if 'Install All' is checked) or from selection dialog
            try
            {
                string[] selected;
                if (chkInstallAll != null && chkInstallAll.Checked)
                {
                    selected = installers;
                }
                else
                {
                    using (var dlg = new SelectInstallersForm(installers))
                    {
                        var dr = dlg.ShowDialog();
                        if (dr != DialogResult.OK)
                        {
                            AddLog("[INFO] Installation cancelled by user (selection dialog). ");
                            return;
                        }

                        selected = dlg.SelectedFiles ?? Array.Empty<string>();
                        if (selected.Length == 0)
                        {
                            MessageBox.Show("No installers selected.");
                            return;
                        }
                    }
                }

                // After selection: check for special folder "revisar" and reorder to install certain installers last
                try
                {
                    var rootFolder = folder;
                    var specialFolder = Path.Combine(rootFolder, "revisar");
                    var specialInstallers = new List<string>();
                    if (Directory.Exists(specialFolder))
                    {
                        AddLog($"[INFO] Found special folder: {specialFolder}");
                        // find installers in special folder
                        specialInstallers = Directory.GetFiles(specialFolder, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (specialInstallers.Count > 0)
                            AddLog($"[INFO] Found {specialInstallers.Count} installer(s) in 'revisar' folder to run after root installers.");
                    }

                    // Determine which installers should be executed at the end
                    var finalOrderNames = new List<string>();

                    // Office detection: any file name containing 'office' (case-insensitive)
                    var officeCandidates = selected.Where(s => Path.GetFileName(s).IndexOf("office", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    bool hasOffice = officeCandidates.Count > 0;
                    if (hasOffice)
                        AddLog("[INFO] Office installer detected; it will be scheduled to run last.");

                    // Specific installers to run after Office (if present) in this order
                    var tailOrderFileNames = new[] {
                        "VisualStudio",
                        "AdobePhotoshop",
                        "AdobeIllustrator",
                        "AdobePremiere",
                        "AdobeAfterEffects",
                        "DaVinci_Resolve_Installer"
                    };

                    // Build sets for quick lookup (file names only)
                    var selectedFileNames = new HashSet<string>(selected.Select(s => Path.GetFileName(s)), StringComparer.OrdinalIgnoreCase);
                    var specialFileNames = new HashSet<string>(specialInstallers.Select(s => Path.GetFileName(s)), StringComparer.OrdinalIgnoreCase);

                    // Identify Office installer (if any) — remove from selected and postpone adding to the very end
                    string? officePath = null;
                    if (hasOffice)
                    {
                        officePath = officeCandidates.First();
                        // remove office from the immediate selected list so it won't run early
                        selected = selected.Where(s => !Path.GetFileName(s).Equals(Path.GetFileName(officePath), StringComparison.OrdinalIgnoreCase)).ToArray();
                        AddLog("[INFO] Office installer detected; it will be scheduled to run last.");
                    }

                    // Then append the specific tail installers if present (from selected or special folder), in the given order
                    foreach (var fname in tailOrderFileNames)
                    {
                        // prefer files from root selection first, then from special folder
                        // Use "contains" style matching (case-insensitive) so partial/similar names are matched
                        string shortName = Path.GetFileNameWithoutExtension(fname);
                        string? match = selected.FirstOrDefault(s =>
                                Path.GetFileName(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                                || Path.GetFileNameWithoutExtension(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                            )
                            ?? specialInstallers.FirstOrDefault(s =>
                                Path.GetFileName(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                                || Path.GetFileNameWithoutExtension(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                            );

                        if (match != null)
                        {
                            finalOrderNames.Add(match);
                            // remove any selected entries that match this fname by contains
                            selected = selected.Where(s =>
                                !(Path.GetFileName(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                                  || Path.GetFileNameWithoutExtension(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0)
                            ).ToArray();
                        }
                    }

                    // After building final tail list, append any installers found in 'revisar' that were not already included
                    foreach (var sp in specialInstallers)
                    {
                        if (!finalOrderNames.Any(f => Path.GetFileName(f).Equals(Path.GetFileName(sp), StringComparison.OrdinalIgnoreCase)))
                        {
                            // only include special folder installers if they were not part of the original selected list and are valid
                            if (!selected.Any(s => Path.GetFileName(s).Equals(Path.GetFileName(sp), StringComparison.OrdinalIgnoreCase)))
                                finalOrderNames.Add(sp);
                        }
                    }

                    // Compose final selection: remaining selected (preserving order), then tail finalOrderNames,
                    // and finally the Office installer (if any) to ensure Office runs last
                    var finalSelectedList = new List<string>();
                    finalSelectedList.AddRange(selected);
                    // append tail installers (finalOrderNames) if not duplicates
                    foreach (var f in finalOrderNames)
                    {
                        if (!finalSelectedList.Any(existing => Path.GetFileName(existing).Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)))
                            finalSelectedList.Add(f);
                    }

                    // Finally append Office installer as the very last item
                    if (!string.IsNullOrEmpty(officePath))
                    {
                        if (!finalSelectedList.Any(existing => Path.GetFileName(existing).Equals(Path.GetFileName(officePath), StringComparison.OrdinalIgnoreCase)))
                            finalSelectedList.Add(officePath!);
                    }

                    selected = finalSelectedList.ToArray();
                    AddLog($"[INFO] Installation order prepared. Total installers: {selected.Length}");
                }
                catch (Exception ex)
                {
                    try { Logger.WriteException(ex, "[WARN] CouldNotPrepareFinalOrder"); } catch { }
                }

                // === UI: Cambiar botones ===
                btnStart.Visible = false;
                btnStop.Visible = true;

                // Start elapsed timer — reset only if label currently has a non-zero value
                try
                {
                    if (!string.IsNullOrEmpty(lblTimer.Text) && lblTimer.Text != "00:00:00")
                    {
                        try { lblTimer.Text = "00:00:00"; } catch { }
                    }
                }
                catch (Exception ex) { Logger.WriteException(ex); }

                startTime = DateTime.Now;
                try { uiTimer.Start(); } catch { }

                cancelToken = new CancellationTokenSource();

                progressBar.Value = 0;
                // Progress is reported as percentage (0-100)
                progressBar.Maximum = 100;

                AddLog("Classifying installers...");
                Logger.Write("Classifying installers...");

                // Run classification on background thread to avoid blocking UI at startup
                var classification = await Task.Run(() =>
                {
                    var lowRisk = new List<string>();
                    var mediumRisk = new List<string>();
                    var highRisk = new List<string>();
                    var rdpFiles = new List<string>();

                    foreach (string file in selected)
                    {
                        if (cancelToken.IsCancellationRequested)
                            break;

                        if (file.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                        {
                            rdpFiles.Add(file);
                            continue;
                        }

                        var level = InstallerService.GetRiskLevel(file);

                        if (level == InstallerService.RiskLevel.LowRisk)
                            lowRisk.Add(file);
                        else if (level == InstallerService.RiskLevel.MediumRisk)
                            mediumRisk.Add(file);
                        else
                            highRisk.Add(file);
                    }

                    return (lowRisk, mediumRisk, highRisk, rdpFiles);
                });
                var lowRisk = classification.lowRisk;
                var mediumRisk = classification.mediumRisk;
                var highRisk = classification.highRisk;
                var rdpFilesList = classification.rdpFiles;

                AddLog($"LowRisk: {lowRisk.Count}, MediumRisk: {mediumRisk.Count}, HighRisk: {highRisk.Count}");
                Logger.Write($"LowRisk: {lowRisk.Count}, MediumRisk: {mediumRisk.Count}, HighRisk: {highRisk.Count}");

                AddLog("Starting installations...");
                Logger.Write("Starting installations...");

                // Reset scheduling info
                InstallerService.ResetSchedule();

                // Pre-copy only the selected installers to temp so we don't copy unselected files
                try
                {
                    InstallerService.PrepareLocalCopies(selected, AddLog);
                }
                catch (Exception ex)
                {
                    Logger.Write("[WARN] PrepareLocalCopies failed: " + ex.Message);
                }

                try
                {
                    Action<int> progressCallback = (current) =>
                    {
                        try
                        {
                            this.Invoke((Delegate)(() =>
                            {
                                try { progressBar.Value = Math.Min(current, progressBar.Maximum); } catch (Exception ex) { Logger.WriteException(ex); }
                                try { progressBarlbl.Text = current.ToString() + "%"; } catch (Exception ex) { Logger.WriteException(ex); }
                            }));
                        }
                        catch (Exception ex) { Logger.WriteException(ex); }
                    };

                    // First run low/medium/rdp in parallel (safe), but DO NOT run highRisk in parallel to avoid Windows Installer conflicts.
                    await InstallerService.InstallAllAsync(lowRisk, mediumRisk, new List<string>(), rdpFilesList, selected.Length, AddLog, progressCallback, cancelToken.Token);

                    // Now run high-risk installers sequentially (one-by-one) to avoid msiexec mutex conflicts
                    if (highRisk.Count > 0)
                    {
                        AddLog("[INFO] Executing HighRisk installers sequentially...");
                        foreach (var hf in highRisk)
                        {
                            if (cancelToken != null && cancelToken.IsCancellationRequested) break;

                            try
                            {
                                AddLog($"[INFO] Starting sequential HighRisk: {Path.GetFileName(hf)}");
                                bool ok = await InstallerService.InstallAsync(hf, AddLog, cancelToken?.Token ?? CancellationToken.None);

                                if (ok)
                                    AddLog($"[DONE] {Path.GetFileName(hf)}");
                                else
                                {
                                    AddLog($"[ERROR] {Path.GetFileName(hf)}");
                                    try { InstallerService.RecordFailure(hf, "HighRisk sequential install failed"); } catch { }
                                }

                                // Update progress using Completed count relative to total selected
                                try
                                {
                                    int completed = InstallerService.Completed.Count;
                                    int percent = (int)Math.Round(100.0 * completed / Math.Max(1, selected.Length));
                                    try { progressCallback?.Invoke(percent); } catch { }
                                }
                                catch { }

                                // Small pause to allow OS to settle and release installer mutexes
                                try { await Task.Delay(2000, cancelToken?.Token ?? CancellationToken.None); } catch { }
                            }
                            catch (OperationCanceledException)
                            {
                                AddLog("[STOP] HighRisk sequential installation cancelled.");
                                break;
                            }
                            catch (Exception ex)
                            {
                                AddLog($"[ERROR] Exception during HighRisk installer {Path.GetFileName(hf)}: {ex.Message}");
                                try { Logger.WriteException(ex, "HighRiskSequential"); } catch { }
                            }
                        }
                    }
                    // Unsubscribe after run (no skip button in this UI version)
                }
                catch (OperationCanceledException)
                {
                    AddLog("[STOP] Installation cancelled by user.");
                }

                AddLog("=== ALL INSTALLATIONS COMPLETE ===");
                Logger.Write("=== ALL INSTALLATIONS COMPLETE ===");

                // Show execution schedule: which installers started in parallel and which were postponed
                AddLog("[SCHEDULE] Started order:");
                Logger.Write("[SCHEDULE] Started order:");
                foreach (var s in InstallerService.StartedOrder)
                {
                    var name = Path.GetFileName(s);
                    AddLog($"  {name}");
                    Logger.Write($"  {name}");
                }

                AddLog("[SCHEDULE] Postponed order:");
                Logger.Write("[SCHEDULE] Postponed order:");
                foreach (var p in InstallerService.PostponedOrder)
                {
                    var name = Path.GetFileName(p);
                    AddLog($"  {name}");
                    Logger.Write($"  {name}");
                }

                // === UI: Restaurar botones ===
                btnStart.Visible = true;
                btnStop.Visible = false;
                cancelToken = null;

                // Stop elapsed timer (keep final elapsed value visible)
                try { uiTimer.Stop(); } catch (Exception ex) { Logger.WriteException(ex); }
            }
            catch (Exception ex)
            {
                Logger.Write("[ERROR] " + ex.ToString());
                MessageBox.Show("An error occurred while preparing installers: " + ex.Message);
            }
        }

        private void btnOpenLog_Click(object sender, EventArgs e)
        {
            string logPath = Logger.LogFilePath;

            if (File.Exists(logPath))
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            else
                MessageBox.Show($"The log file does not exist yet. Expected at:\n{logPath}");
        }


        private void AddLog(string message)
        {
            try
            {
                listLog.Invoke(() =>
                {
                    listLog.Items.Add(message);
                    listLog.TopIndex = listLog.Items.Count - 1;
                });
            }
            catch (Exception ex) { Logger.WriteException(ex); }

            // Also persist UI-visible log lines to the disk log so they can be reported
            try
            {
                Logger.Write(message);
            }
            catch (Exception ex)
            {
                try
                {
                    var fallback = Path.Combine(Path.GetTempPath(), "AutoInstallerApp_logger_fallback.txt");
                    File.AppendAllText(fallback, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AddLog->Logger.Write failed: {ex}{Environment.NewLine}");
                }
                catch { }
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            if (cancelToken != null)
            {
                cancelToken.Cancel();
                AddLog("[STOP] Cancelling installation...");
            }

            btnStop.Visible = false;
            btnStart.Visible = true;

            // Kill all active installer processes started by the app
            try
            {
                InstallerService.KillAllActiveProcesses();
                AddLog("[STOP] Killed all active installer processes.");
                // Stop elapsed timer (keep final elapsed value visible)
                try { uiTimer.Stop(); } catch (Exception ex) { Logger.WriteException(ex); }
            }
            catch (Exception ex)
            {
                Logger.Write("[STOP KILL ERROR] " + ex.ToString());
            }
        }
    }
}
