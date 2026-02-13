using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;

namespace AutoInstallerApp
{
    public partial class Form1 : Form
    {
        private CancellationTokenSource cancelToken;
        private bool isInstalling = false;

        public Form1()
        {
            InitializeComponent();
            btnStop.Visible = false; // Asegurar que STOP esté oculto al inicio
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

            // === UI: Cambiar botones ===
            btnStart.Visible = false;
            btnStop.Visible = true;
            isInstalling = true;

            cancelToken = new CancellationTokenSource();

            progressBar.Value = 0;
            progressBar.Maximum = installers.Length;

            AddLog("Classifying installers...");
            Logger.Write("Classifying installers...");

            var lowRisk = new List<string>();
            var mediumRisk = new List<string>();
            var highRisk = new List<string>();

            foreach (string file in installers)
            {
                if (cancelToken.IsCancellationRequested)
                    break;

                string name = Path.GetFileName(file);

                // RDP → copiar al escritorio del usuario actual
                if (file.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string dest = Path.Combine(desktop, name);

                    File.Copy(file, dest, true);

                    AddLog($"[RDP COPIED] {name}");
                    Logger.Write($"[RDP COPIED] {name}");

                    progressBar.Value++;
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

            AddLog($"LowRisk: {lowRisk.Count}, MediumRisk: {mediumRisk.Count}, HighRisk: {highRisk.Count}");
            Logger.Write($"LowRisk: {lowRisk.Count}, MediumRisk: {mediumRisk.Count}, HighRisk: {highRisk.Count}");

            AddLog("Starting installations...");
            Logger.Write("Starting installations...");

            try
            {
                await InstallerService.InstallAllAsync(lowRisk, mediumRisk, highRisk, AddLog, cancelToken.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog("[STOP] Installation cancelled by user.");
            }

            AddLog("=== ALL INSTALLATIONS COMPLETE ===");
            Logger.Write("=== ALL INSTALLATIONS COMPLETE ===");

            // === UI: Restaurar botones ===
            btnStart.Visible = true;
            btnStop.Visible = false;
            isInstalling = false;
            cancelToken = null;
        }

        private void btnOpenLog_Click(object sender, EventArgs e)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer_log.txt");

            if (File.Exists(logPath))
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            else
                MessageBox.Show("The log file does not exist yet.");
        }

        private void AddLog(string message)
        {
            listLog.Invoke(() =>
            {
                listLog.Items.Add(message);
                listLog.TopIndex = listLog.Items.Count - 1;
            });
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
            isInstalling = false;
        }
    }
}
