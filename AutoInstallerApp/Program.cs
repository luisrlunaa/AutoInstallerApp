namespace AutoInstallerApp
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Global exception handlers so we can capture startup crashes (useful for single-file apps)
            Application.ThreadException += (s, e) =>
            {
                try { Logger.Write("[UI EXCEPTION] " + e.Exception.ToString()); } catch { }
                try { MessageBox.Show("An unexpected error occurred:\n" + e.Exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Logger.Write("[UNHANDLED EXCEPTION] " + (e.ExceptionObject?.ToString() ?? "(null)")); } catch { }
            };

            try
            {
                // To customize application configuration such as set high DPI settings or default font,
                // see https://aka.ms/applicationconfiguration.
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                try { Logger.Write("[FATAL] " + ex.ToString()); } catch { }
                try { MessageBox.Show("The application failed to start:\n" + ex.Message, "Fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            }
        }
    }
}