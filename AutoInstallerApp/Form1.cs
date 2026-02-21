using System.Diagnostics;

namespace AutoInstallerApp
{
    public partial class Form1 : Form
    {
        private CancellationTokenSource? cancelToken;

        public Form1()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                try { Logger.Write("[INIT ERROR] " + ex.ToString()); } catch { }
                throw;
            }

            // Ensure STOP hidden initially
            try { btnStop.Visible = false; } catch { }

            // Ensure progress label starts at 0% and is visible in front of the progress bar
            try
            {
                progressBarlbl.Text = "0%";
                progressBarlbl.Visible = true;
                progressBarlbl.BringToFront();
            }
            catch { }

            this.Load += Form1_Load;
            // Timer for elapsed time display
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1000; // 1 second
            uiTimer.Tick += UiTimer_Tick;
        }

        private System.Windows.Forms.Timer uiTimer;
        private DateTime? startTime;

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

            try
            {
                // UI automation flag is controlled internally; checkbox removed from UI
            }
            catch { }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            string folder = txtFolder.Text;

            if (!Directory.Exists(folder))
            {
                MessageBox.Show("The folder does not exist.");
                return;
            }

            string[] installers = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (installers.Length == 0)
            {
                MessageBox.Show("No installers found.");
                return;
            }

            // Show checklist dialog so user can uncheck installers they don't want
            try
            {
                using (var dlg = new SelectInstallersForm(installers))
                {
                    var dr = dlg.ShowDialog();
                    if (dr != DialogResult.OK)
                    {
                        AddLog("[INFO] Installation cancelled by user (selection dialog). ");
                        return;
                    }

                    var selected = dlg.SelectedFiles ?? Array.Empty<string>();
                    if (selected.Length == 0)
                    {
                        MessageBox.Show("No installers selected.");
                        return;
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
                    catch { }

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
                                    try { progressBar.Value = Math.Min(current, progressBar.Maximum); } catch { }
                                    try { progressBarlbl.Text = current.ToString() + "%"; } catch { }
                                }));
                            }
                            catch { }
                        };

                        await InstallerService.InstallAllAsync(lowRisk, mediumRisk, highRisk, rdpFilesList, selected.Length, AddLog, progressCallback, cancelToken.Token);
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
                    try { uiTimer.Stop(); } catch { }
                }
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
            catch { }

            // Also persist UI-visible log lines to the disk log so they can be reported
            try
            {
                Logger.Write(message);
            }
            catch { }
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
                try { uiTimer.Stop(); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Write("[STOP KILL ERROR] " + ex.ToString());
            }
        }
    }
}
