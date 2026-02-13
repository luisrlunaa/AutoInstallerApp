using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AutoInstallerApp
{
    public static class InstallerService
    {
        public enum RiskLevel
        {
            LowRisk,
            MediumRisk,
            HighRisk
        }

        // ============================
        // CLASIFICACIÓN AUTOMÁTICA
        // ============================
        public static RiskLevel GetRiskLevel(string file)
        {
            string name = Path.GetFileName(file).ToLower();

            if (file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                return RiskLevel.HighRisk;

            string[] highRiskKeywords =
            {
                "office", "sophos", "tsprint", "tsscan",
                "erad", "vnc", "tightvnc", "agent", "driver"
            };

            if (highRiskKeywords.Any(k => name.Contains(k)))
                return RiskLevel.HighRisk;

            string[] mediumRiskKeywords =
            {
                "chrome", "adobe", "reader", "acrobat",
                "java", "jre", "jre_", "jre-", "setup", "installer", "update"
            };

            if (mediumRiskKeywords.Any(k => name.Contains(k)))
                return RiskLevel.MediumRisk;

            string content = string.Empty;
            try { content = File.ReadAllText(file); }
            catch { return RiskLevel.MediumRisk; }

            if (content.Contains("Inno Setup") ||
                content.Contains("Nullsoft") ||
                content.Contains("InstallShield"))
                return RiskLevel.LowRisk;

            return RiskLevel.MediumRisk;
        }

        // ============================
        // INSTALACIÓN PRINCIPAL
        // ============================
        public static async Task InstallAllAsync(
            List<string> lowRisk,
            List<string> mediumRisk,
            List<string> highRisk,
            Action<string> logCallback,
            CancellationToken token)
        {
            if (lowRisk.Count > 0)
            {
                logCallback("[INFO] Starting LOW-RISK installers in parallel...");

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 3,
                    CancellationToken = token
                };

                await Task.Run(() =>
                {
                    Parallel.ForEach(lowRisk, parallelOptions, file =>
                    {
                        if (!token.IsCancellationRequested)
                            InstallAsync(file, logCallback, token).Wait();
                    });
                });
            }

            if (mediumRisk.Count > 0)
            {
                logCallback("[INFO] Starting MEDIUM-RISK installers sequentially...");

                foreach (string file in mediumRisk)
                {
                    if (token.IsCancellationRequested)
                        return;

                    await InstallAsync(file, logCallback, token);
                }
            }

            if (highRisk.Count > 0)
            {
                logCallback("[INFO] Starting HIGH-RISK installers sequentially...");

                foreach (string file in highRisk)
                {
                    if (token.IsCancellationRequested)
                        return;

                    await InstallAsync(file, logCallback, token);
                }
            }
        }

        // ============================
        // INSTALAR UN SOLO ARCHIVO
        // ============================
        public static async Task InstallAsync(string file, Action<string> logCallback, CancellationToken token)
        {
            await Task.Run(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                string name = Path.GetFileName(file);
                logCallback($"[INSTALLING] {name}");

                try
                {
                    WaitForInstallerToBeFree(logCallback, token);

                    if (token.IsCancellationRequested)
                        return;

                    bool success = RunInstaller(file, logCallback, elevated: false, token);

                    if (!success && !token.IsCancellationRequested)
                    {
                        logCallback($"[INFO] Retrying with administrator privileges: {name}");
                        WaitForInstallerToBeFree(logCallback, token);

                        if (!token.IsCancellationRequested)
                            RunInstaller(file, logCallback, elevated: true, token);
                    }

                    if (!token.IsCancellationRequested)
                        logCallback($"[DONE] {name}");
                }
                catch (Exception ex)
                {
                    logCallback($"[EXCEPTION] {file}: {ex.Message}");
                }
            });
        }

        // ============================
        // ESPERA INTELIGENTE
        // ============================
        private static void WaitForInstallerToBeFree(Action<string> logCallback, CancellationToken token)
        {
            int waitTime = 500;
            int maxStepWait = 15000;
            int maxTotalWait = 60000;
            int waited = 0;

            while (IsAnotherInstallerRunning())
            {
                if (token.IsCancellationRequested)
                    return;

                if (waited >= maxTotalWait)
                {
                    logCallback("[WARN] Max wait reached. Continuing anyway.");
                    break;
                }

                logCallback($"[WAIT] Another installer is running. Waiting {waitTime}ms...");
                Thread.Sleep(waitTime);

                waited += waitTime;
                waitTime = Math.Min(waitTime + 500, maxStepWait);
            }
        }

        private static bool IsAnotherInstallerRunning()
        {
            string[] installerProcesses =
            {
                "msiexec",
                // "OfficeClickToRun",  ← REMOVIDO (OfficeClickToRun SIEMPRE está activo)
                "GoogleUpdate",
                "setup",
                "installer",
                "SophosInstall",
                "TSPrint",
                "TSScan"
            };

            return installerProcesses.Any(p => Process.GetProcessesByName(p).Length > 0);
        }

        // ============================
        // EJECUTAR INSTALADOR (Office + No silenciosos)
        // ============================
        private static bool RunInstaller(string file, Action<string> logCallback, bool elevated, CancellationToken token)
        {
            try
            {
                string exeName = Path.GetFileName(file).ToLower();

                // Instaladores que NO aceptan parámetros silenciosos
                string[] nonSilentInstallers =
                {
                    "chrome", "firefox", "edge", "anydesk",
                    "teamviewer", "zoom", "acro", "reader",
                    "java", "jre", "winrar"
                };

                // ============================
                // CASO ESPECIAL: OFFICE
                // ============================
                if (exeName.Contains("office"))
                {
                    logCallback("[INFO] Office installer detected → running from original location without parameters");

                    Process office = new Process();
                    office.StartInfo.FileName = file;
                    office.StartInfo.Arguments = ""; // Office NO acepta /silent
                    office.StartInfo.UseShellExecute = true;
                    office.StartInfo.CreateNoWindow = false;

                    if (elevated)
                        office.StartInfo.Verb = "runas";

                    office.Start();

                    while (!office.HasExited)
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { office.Kill(); } catch { }
                            return false;
                        }

                        Thread.Sleep(200);
                    }

                    return office.ExitCode == 0;
                }

                // ============================
                // OTROS INSTALADORES
                // ============================
                string tempFolder = Path.Combine(Path.GetTempPath(), "AutoInstaller");
                Directory.CreateDirectory(tempFolder);

                string localFile = Path.Combine(tempFolder, Path.GetFileName(file));
                File.Copy(file, localFile, true);

                logCallback($"[INFO] Copied to local temp: {localFile}");

                Process process = new Process();

                if (localFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = localFile;

                    if (nonSilentInstallers.Any(k => exeName.Contains(k)))
                    {
                        process.StartInfo.Arguments = "";
                        logCallback("[INFO] Running without silent parameters (non-silent installer detected)");
                    }
                    else
                    {
                        process.StartInfo.Arguments = "/silent /verysilent /quiet /norestart";
                    }
                }
                else if (localFile.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    process.StartInfo.FileName = "msiexec.exe";
                    process.StartInfo.Arguments = $"/i \"{localFile}\" /qn /norestart";
                }

                process.StartInfo.UseShellExecute = true;
                process.StartInfo.CreateNoWindow = true;

                if (elevated)
                    process.StartInfo.Verb = "runas";

                process.Start();

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }

                    Thread.Sleep(200);
                }

                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
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
