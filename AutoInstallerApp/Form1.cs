// Form1.cs
using AutoInstallerApp.Language;
using System.Diagnostics;

namespace AutoInstallerApp
{
    public partial class Form1 : Form
    {
        private CancellationTokenSource? cancelToken;
        private bool initializationFailed = false;
        private System.Windows.Forms.Timer uiTimer;
        private DateTime? startTime;

        // **Total number of installers used for progress calculations**
        // Set once when the user starts the run and used to compute percent.
        private int totalInstallers = 1;

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

            // Make progress bar smooth where possible
            try
            {
                progressBar.Style = ProgressBarStyle.Continuous;
            }
            catch { }
        }

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

            var installersList = new List<string>();
            try
            {
                installersList.AddRange(Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)));

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

            try
            {
                AddLog("[DEBUG] Discovered installers:");
                foreach (var it in installers)
                {
                    AddLog("  " + it);
                }
            }
            catch { }

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

                try
                {
                    var rootFolder = folder;
                    var specialFolder = Path.Combine(rootFolder, "revisar");
                    var specialInstallers = new List<string>();
                    if (Directory.Exists(specialFolder))
                    {
                        AddLog($"[INFO] Found special folder: {specialFolder}");
                        specialInstallers = Directory.GetFiles(specialFolder, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (specialInstallers.Count > 0)
                            AddLog($"[INFO] Found {specialInstallers.Count} installer(s) in 'revisar' folder to run after root installers.");
                    }

                    var finalOrderNames = new List<string>();
                    var officeCandidates = selected.Where(s => Path.GetFileName(s).IndexOf("office", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    bool hasOffice = officeCandidates.Count > 0;

                    var tailOrderFileNames = new[] {
                "VisualStudio", "AdobePhotoshop", "AdobeIllustrator",
                "AdobePremiere", "AdobeAfterEffects", "DaVinci_Resolve_Installer"
            };

                    string? officePath = null;
                    if (hasOffice)
                    {
                        officePath = officeCandidates.First();
                        selected = selected.Where(s => !Path.GetFileName(s).Equals(Path.GetFileName(officePath), StringComparison.OrdinalIgnoreCase)).ToArray();
                        AddLog("[INFO] Office installer detected; it will be scheduled to run last.");
                    }

                    foreach (var fname in tailOrderFileNames)
                    {
                        string shortName = Path.GetFileNameWithoutExtension(fname);
                        string? match = selected.FirstOrDefault(s =>
                                Path.GetFileName(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                                || Path.GetFileNameWithoutExtension(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? specialInstallers.FirstOrDefault(s =>
                                Path.GetFileName(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                                || Path.GetFileNameWithoutExtension(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0);

                        if (match != null)
                        {
                            finalOrderNames.Add(match);
                            selected = selected.Where(s =>
                                !(Path.GetFileName(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0
                                  || Path.GetFileNameWithoutExtension(s).IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                        }
                    }

                    foreach (var sp in specialInstallers)
                    {
                        if (!finalOrderNames.Any(f => Path.GetFileName(f).Equals(Path.GetFileName(sp), StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!selected.Any(s => Path.GetFileName(s).Equals(Path.GetFileName(sp), StringComparison.OrdinalIgnoreCase)))
                                finalOrderNames.Add(sp);
                        }
                    }

                    var finalSelectedList = new List<string>();
                    finalSelectedList.AddRange(selected);
                    foreach (var f in finalOrderNames)
                    {
                        if (!finalSelectedList.Any(existing => Path.GetFileName(existing).Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)))
                            finalSelectedList.Add(f);
                    }

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

                btnStart.Visible = false;
                btnStop.Visible = true;

                try
                {
                    if (!string.IsNullOrEmpty(lblTimer.Text) && lblTimer.Text != "00:00:00")
                    {
                        lblTimer.Text = "00:00:00";
                    }
                }
                catch (Exception ex) { Logger.WriteException(ex); }

                startTime = DateTime.Now;
                try { uiTimer.Start(); } catch { }

                cancelToken = new CancellationTokenSource();

                // Reset ProgressBar: use installer count as Maximum for accurate progress
                totalInstallers = Math.Max(1, selected.Length);
                try
                {
                    progressBar.Maximum = totalInstallers;
                    progressBar.Value = 0;
                }
                catch
                {
                    // If the control doesn't accept a small maximum, fall back to 100-based percent
                    progressBar.Maximum = 100;
                    progressBar.Value = 0;
                }

                UpdateProgress(0);

                AddLog("Classifying installers...");
                var classification = await Task.Run(() =>
                {
                    var lowRisk = new List<string>();
                    var mediumRisk = new List<string>();
                    var highRisk = new List<string>();
                    var rdpFiles = new List<string>();

                    foreach (string file in selected)
                    {
                        if (cancelToken.IsCancellationRequested) break;

                        if (file.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                        {
                            rdpFiles.Add(file);
                            continue;
                        }

                        var level = InstallerService.GetRiskLevel(file);
                        if (level == InstallerService.RiskLevel.LowRisk) lowRisk.Add(file);
                        else if (level == InstallerService.RiskLevel.MediumRisk) mediumRisk.Add(file);
                        else highRisk.Add(file);
                    }
                    return (lowRisk, mediumRisk, highRisk, rdpFiles);
                });

                var lowRisk = classification.lowRisk;
                var mediumRisk = classification.mediumRisk;
                var highRisk = classification.highRisk;
                var rdpFilesList = classification.rdpFiles;

                AddLog("Starting installations...");
                InstallerService.ResetSchedule();

                try { InstallerService.PrepareLocalCopies(selected, AddLog); }
                catch (Exception ex) { Logger.Write("[WARN] PrepareLocalCopies failed: " + ex.Message); }

                // Progress callback: update based on InstallerService.Completed count
                Action<int> progressCallback = (_) =>
                {
                    try
                    {
                        int completedCount = InstallerService.Completed.Count;
                        // If progressBar.Maximum equals totalInstallers, set Value directly
                        try
                        {
                            if (progressBar.Maximum == totalInstallers)
                            {
                                int value = Math.Min(progressBar.Maximum, Math.Max(0, completedCount));
                                // Ensure UI thread update
                                UpdateProgressBarValue(value, totalInstallers);
                            }
                            else
                            {
                                // Fallback: compute percent and set label
                                int percent = (int)Math.Round(100.0 * completedCount / totalInstallers);
                                UpdateProgress(percent);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteException(ex, "progressCallback:update");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteException(ex, "progressCallback");
                    }
                };

                try
                {
                    // Execute parallel batch
                    await InstallerService.InstallAllAsync(lowRisk, mediumRisk, new List<string>(), rdpFilesList, selected.Length, AddLog, progressCallback, cancelToken.Token);

                    // Execute high-risk sequentially
                    if (highRisk.Count > 0)
                    {
                        AddLog("[INFO] Executing HighRisk installers sequentially...");
                        foreach (var hf in highRisk)
                        {
                            if (cancelToken.IsCancellationRequested) break;

                            AddLog($"[INFO] Starting sequential HighRisk: {Path.GetFileName(hf)}");
                            bool ok = await InstallerService.InstallAsync(hf, AddLog, cancelToken.Token);

                            if (ok) AddLog($"[DONE] {Path.GetFileName(hf)}");
                            else AddLog($"[ERROR] {Path.GetFileName(hf)}");

                            // Update progress based on InstallerService.Completed
                            try
                            {
                                int completedCount = InstallerService.Completed.Count;
                                UpdateProgressBarValue(Math.Min(completedCount, progressBar.Maximum), totalInstallers);
                            }
                            catch (Exception ex) { Logger.WriteException(ex, "HighRiskProgressUpdate"); }

                            await Task.Delay(2000, cancelToken.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    AddLog("[STOP] Installation cancelled by user.");
                }

                // Final ensure 100% if all completed
                try
                {
                    int finalCompleted = InstallerService.Completed.Count;
                    if (finalCompleted >= totalInstallers)
                    {
                        UpdateProgressBarValue(progressBar.Maximum, totalInstallers);
                        UpdateProgress(100);
                    }
                    else
                    {
                        UpdateProgressBarValue(Math.Min(finalCompleted, progressBar.Maximum), totalInstallers);
                        int percent = (int)Math.Round(100.0 * finalCompleted / totalInstallers);
                        UpdateProgress(percent);
                    }
                }
                catch { UpdateProgress(100); }

                AddLog("=== ALL INSTALLATIONS COMPLETE ===");

                // Final UI restoration
                btnStart.Visible = true;
                btnStop.Visible = false;
                cancelToken = null;
                try { uiTimer.Stop(); } catch (Exception ex) { Logger.WriteException(ex); }
            }
            catch (Exception ex)
            {
                Logger.Write("[ERROR] " + ex.ToString());
                MessageBox.Show("An error occurred: " + ex.Message);
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

        /// <summary>
        /// Thread-safe update of the progress bar when Maximum == totalInstallers.
        /// Sets the progressBar.Value and updates the percent label.
        /// </summary>
        private void UpdateProgressBarValue(int value, int total)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateProgressBarValue(value, total)));
                return;
            }

            try
            {
                // Ensure bounds
                if (progressBar.Maximum <= 0)
                {
                    progressBar.Maximum = Math.Max(1, total);
                }

                int safeValue = Math.Min(progressBar.Maximum, Math.Max(0, value));
                progressBar.Value = safeValue;

                int percent = (int)Math.Round(100.0 * safeValue / Math.Max(1, total));
                progressBarlbl.Text = $"{percent}%";
            }
            catch (Exception ex)
            {
                Logger.WriteException(ex, "UpdateProgressBarValue");
            }
        }

        private void UpdateProgress(int value)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateProgress(value)));
                return;
            }

            try
            {
                // Ensure value stays within 0 - Maximum (when Maximum is 100)
                if (value > progressBar.Maximum) value = progressBar.Maximum;
                if (value < 0) value = 0;

                // Smoothly update the progress bar value
                try
                {
                    progressBar.Value = value;
                }
                catch
                {
                    progressBar.Value = Math.Min(progressBar.Maximum, Math.Max(0, value));
                }

                // Update the label with the calculated percentage
                progressBarlbl.Text = $"{value}%";
            }
            catch (Exception ex)
            {
                Logger.WriteException(ex, "UpdateProgressUI");
            }
        }
    }
}
