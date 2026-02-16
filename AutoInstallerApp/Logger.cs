using System;
using System.IO;

namespace AutoInstallerApp
{
    public static class Logger
    {
        // Use LocalApplicationData so single-file or limited-permissions runs can still write logs
        private static readonly string LogPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoInstallerApp", "installer_log.txt");

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
            catch(Exception ex)
            {
                MessageBox.Show("Swallow any logging errors to avoid crashing the app when logging isn't possible: => ", ex.Message);
            }
        }
    }
}
