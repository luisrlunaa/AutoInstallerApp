using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace AutoInstallerApp
{
    public static class InstallerUiAutomator
    {
        private static readonly string[] CommonButtonNames = new[]
        {
            "next", "siguiente", "install", "instalar", "finish", "done",
            "close", "ok", "accept", "aceptar", "yes", "si", "continue",
            "continuar", "aceptar y continuar"
        };

        private static readonly string[] AcceptCheckboxTexts = new[]
        {
            "i accept", "i agree", "acepto", "acepto los", "acepto los terminos", "aceptar"
        };

        private static readonly string[] NegativeButtonWords = new[]
        {
            "cancel", "cancelar", "decline", "no", "deny", "rechazar"
        };

        private static readonly string[] PasswordFieldWords = new[]
        {
            "password", "contraseña", "contrase", "clave", "pwd"
        };

        private static readonly string[] LicenseFieldWords = new[]
        {
            "license", "licencia", "serial", "product key", "productkey",
            "clave del producto", "cd key", "cdkey", "número de serie"
        };

        public static void InteractWithProcess(int pid, Action<string> logCallback, CancellationToken token, int timeoutMs = 120000)
        {
            Task.Run(() => InteractWithProcessAsync(pid, logCallback, token, timeoutMs), token)
                .ConfigureAwait(false);
        }

        private static async Task InteractWithProcessAsync(int pid, Action<string> logCallback, CancellationToken token, int timeoutMs)
        {
            try
            {
                using var automation = new UIA3Automation();

                FlaUI.Core.Application app;
                try
                {
                    app = FlaUI.Core.Application.Attach(pid);
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[AUTOMATOR] Could not attach to PID {pid}: {ex.Message}");
                    return;
                }

                var end = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);

                while (!token.IsCancellationRequested && DateTime.UtcNow < end)
                {
                    if (app.HasExited)
                    {
                        logCallback?.Invoke($"[AUTOMATOR] Process {pid} exited.");
                        return;
                    }

                    bool actionTaken = false;

                    Window[] windows;
                    try
                    {
                        windows = app.GetAllTopLevelWindows(automation);
                    }
                    catch
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    foreach (var win in windows)
                    {
                        if (win == null) continue;

                        string title = win.Title ?? "";
                        if (title.Contains("User Account Control", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 1) Toggle acceptance checkboxes
                        var checkboxes = win.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                        foreach (var cb in checkboxes)
                        {
                            if (AcceptCheckboxTexts.Any(t => (cb.Name ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)))
                            {
                                try
                                {
                                    var box = cb.AsCheckBox();
                                    if (box != null && (box.IsChecked == false || box.IsChecked == null))
                                    {
                                        box.Toggle();
                                        logCallback?.Invoke($"[AUTOMATOR] Checkbox toggled: {cb.Name}");
                                        actionTaken = true;
                                    }
                                }
                                catch { }
                            }
                        }

                        // 2) Detect buttons
                        var buttons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));

                        foreach (var b in buttons)
                        {
                            if (b == null) continue;

                            string name = b.Name ?? "";

                            if (!CommonButtonNames.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            if (NegativeButtonWords.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            // === FALLBACK 1: Invoke() ===
                            try
                            {
                                var btn = b.AsButton();
                                if (btn != null && btn.IsEnabled)
                                {
                                    btn.Invoke();
                                    logCallback?.Invoke($"[AUTOMATOR] Invoke() → {name}");
                                    actionTaken = true;
                                    await Task.Delay(700);
                                    continue;
                                }
                            }
                            catch { }

                            // === FALLBACK 2: LegacyIAccessible ===
                            try
                            {
                                var legacy = b.Patterns.LegacyIAccessible.PatternOrDefault;
                                if (legacy != null)
                                {
                                    legacy.DoDefaultAction();
                                    logCallback?.Invoke($"[AUTOMATOR] LegacyIAccessible → {name}");
                                    actionTaken = true;
                                    await Task.Delay(700);
                                    continue;
                                }
                            }
                            catch { }

                            // === FALLBACK 3: Click por coordenadas ===
                            try
                            {
                                var rect = b.BoundingRectangle;
                                if (!rect.IsEmpty)
                                {
                                    int x = (int)(rect.Left + rect.Width / 2);
                                    int y = (int)(rect.Top + rect.Height / 2);

                                    FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
                                    FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);


                                    logCallback?.Invoke($"[AUTOMATOR] Coordinate click → {name} ({x},{y})");
                                    actionTaken = true;
                                    await Task.Delay(700);
                                    continue;
                                }
                            }
                            catch { }
                        }
                    }

                    if (!actionTaken)
                        await Task.Delay(800);
                }

                logCallback?.Invoke($"[AUTOMATOR] Timeout reached for PID {pid}");
            }
            catch { }
        }
    }
}
