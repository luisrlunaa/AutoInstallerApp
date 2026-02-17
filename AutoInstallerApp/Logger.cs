namespace AutoInstallerApp
{
    public static class Logger
    {
        // Log file placed at the root of the drive where Windows is installed
        private static readonly string LogPath;

        static Logger()
        {
            try
            {
                // Get Windows directory (e.g. C:\Windows) and extract its root (e.g. C:\)
                string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "C:\\";
                string root = Path.GetPathRoot(windowsDir) ?? "C:\\";
                LogPath = Path.Combine(root, "AutoInstallerApp", "installer_log.txt");
            }
            catch
            {
                // Fallback to C:\ if anything fails
                LogPath = Path.Combine("C:\\", "AutoInstallerApp", "installer_log.txt");
            }
        }

        // Public accessor for other parts of the app to open the log file
        public static string LogFilePath => LogPath;

        public static void Write(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Swallow any logging errors to avoid crashing the app when logging isn't possible: => ", ex.Message);
            }
        }
    }
}
