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

        private static readonly object _writeLock = new object();

        public static void Write(string message,
            [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0,
            [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string fileName = string.Empty;
                try { fileName = Path.GetFileName(callerFile); } catch { }

                // Compact format: include line and class (file) and member for context
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] line: {callerLine}, class: {fileName}, member: {callerMember} - {message}";

                lock (_writeLock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Swallow any logging errors to avoid crashing the app when logging isn't possible
            }
        }

        public static void WriteException(Exception ex, string? context = null,
            [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0,
            [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
        {
            try
            {
                string ctx = string.IsNullOrWhiteSpace(context) ? string.Empty : (context + " - ");
                string message = $"{ctx}EXCEPTION: {ex}";
                // Write will prepend line/class/member using caller info provided
                Write(message, callerFile, callerLine, callerMember);
            }
            catch
            {
                // Swallow any logging errors
            }
        }
    }
}
