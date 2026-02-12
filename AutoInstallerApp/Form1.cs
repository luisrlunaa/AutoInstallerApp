using System.Diagnostics;

namespace AutoInstallerApp
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
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
                            f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (installers.Length == 0)
            {
                MessageBox.Show("No installers found.");
                return;
            }

            progressBar.Value = 0;
            progressBar.Maximum = installers.Length;

            AddLog("Starting sequential installations...");
            Logger.Write("Starting sequential installations...");

            // Ejecutar uno por uno
            foreach (string file in installers)
            {
                string name = Path.GetFileName(file);

                AddLog($"Starting: {name}");
                Logger.Write($"Starting: {name}");

                await InstallerService.InstallAsync(file, AddLog);

                progressBar.Value++;
            }

            AddLog("=== ALL INSTALLATIONS COMPLETE ===");
            Logger.Write("=== ALL INSTALLATIONS COMPLETE ===");
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
    }
}
