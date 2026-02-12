using System.Diagnostics;

namespace AutoInstallerApp
{
    public static class InstallerService
    {
        public static async Task InstallAsync(string file, Action<string> logCallback)
        {
            await Task.Run(() =>
            {
                string name = Path.GetFileName(file);

                try
                {
                    // 1. Intento normal
                    bool success = RunInstaller(file, logCallback, elevated: false);

                    if (!success)
                    {
                        logCallback($"[INFO] Retrying with administrator privileges: {name}");
                        Logger.Write($"[INFO] Retrying with administrator privileges: {name}");

                        // 2. Reintento con elevación (UAC)
                        RunInstaller(file, logCallback, elevated: true);
                    }

                    logCallback($"[DONE] {name}");
                    Logger.Write($"[DONE] {name}");
                }
                catch (Exception ex)
                {
                    logCallback($"[EXCEPTION] {file}: {ex.Message}");
                    Logger.Write($"[EXCEPTION] {file}: {ex}");
                }
            });
        }

        private static bool RunInstaller(string file, Action<string> logCallback, bool elevated)
        {
            try
            {
                Process process = new Process();

                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = file;
                    process.StartInfo.Arguments = "/silent /verysilent /quiet /norestart";
                }
                else if (file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = "msiexec.exe";
                    process.StartInfo.Arguments = $"/i \"{file}\" /qn /norestart";
                }

                process.StartInfo.UseShellExecute = true;
                process.StartInfo.CreateNoWindow = true;

                if (elevated)
                    process.StartInfo.Verb = "runas"; // UAC prompt

                process.Start();
                process.WaitForExit();

                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Error 740 = requiere elevación
                if (ex.NativeErrorCode == 740)
                {
                    logCallback("[INFO] Installer requires elevation.");
                    return false;
                }

                throw;
            }
        }
    }
}
