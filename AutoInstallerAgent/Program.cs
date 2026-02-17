using System;
using System.Diagnostics;
using System.Security.Principal;
using AutoInstallerApp;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("[AGENT] AutoInstallerAgent started.");

            if (args.Length == 0 || !int.TryParse(args[0], out int pid))
            {
                Console.WriteLine("[AGENT] Usage: AutoInstallerAgent.exe <PID>");
                return 1;
            }

            // Confirm elevation
            if (!IsRunningAsAdmin())
            {
                Console.WriteLine("[AGENT] WARNING: Agent is NOT running as administrator.");
                Console.WriteLine("[AGENT] UI automation on elevated installers may fail.");
            }
            else
            {
                Console.WriteLine("[AGENT] Running with administrator privileges.");
            }

            Logger.Write($"[AGENT] Starting automation for PID {pid}");

            // Run the automator in blocking mode
            int result = InstallerUiAutomator.RunAgentBlocking(pid, timeoutMs: 180000);

            Logger.Write($"[AGENT] Finished automation for PID {pid} with code {result}");
            Console.WriteLine($"[AGENT] Completed with code {result}");

            return result;
        }
        catch (Exception ex)
        {
            try { Logger.Write("[AGENT ERROR] " + ex.ToString()); } catch { }
            Console.WriteLine("[AGENT ERROR] " + ex.Message);
            return 1;
        }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
