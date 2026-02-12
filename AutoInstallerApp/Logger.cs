namespace AutoInstallerApp
{
    public static class Logger
    {
        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer_log.txt");

        public static void Write(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
