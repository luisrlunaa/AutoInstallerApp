using AutoInstallerApp.Language;

namespace AutoInstallerApp
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // If started with --agent, run agent loop and exit
            try
            {
                if (args != null && args.Length > 0 && args[0] == "--agent")
                {
                    // Run elevated agent loop
                    try
                    {
                        AutoInstallerAgent.AgentMain.RunAgentLoop();
                    }
                    catch (Exception ex)
                    {
                        try { Logger.WriteException(ex, "[AGENT FATAL]"); } catch { }
                    }

                    return;
                }
            }
            catch (Exception ex) { Logger.WriteException(ex); }
            // Global exception handlers so we can capture startup crashes (useful for single-file apps)
            Application.ThreadException += (s, e) =>
            {
                try { Logger.WriteException(e.Exception, "[UI EXCEPTION]"); } catch { }
                try { MessageBox.Show("An unexpected error occurred:\n" + e.Exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch (Exception ex) { Logger.WriteException(ex); }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Logger.Write("[UNHANDLED EXCEPTION] " + (e.ExceptionObject?.ToString() ?? "(null)")); } catch (Exception ex) { Logger.WriteException(ex); }
            };

            try
            {
                // To customize application configuration such as set high DPI settings or default font,
                // see https://aka.ms/applicationconfiguration.
                ApplicationConfiguration.Initialize();
                LanguageManager.Initialize();
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "[FATAL]"); } catch { }
                try { MessageBox.Show("The application failed to start:\n" + ex.Message, "Fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch (Exception logEx) { Logger.WriteException(logEx); }
            }
        }
    }
}