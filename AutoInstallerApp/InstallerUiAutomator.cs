using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace AutoInstallerApp
{
    public static class InstallerUiAutomator
    {
        // When enabled, the automator will press "final" buttons (Finish/Done/Close)
        // even if other actionable buttons are present after a short wait. This
        // helps fully-automatic flows where human confirmation is not desired.
        public static bool AggressiveFinalClick = true;
        // Maximum wait (ms) before forcing a final-only button click when in aggressive mode
        public static int AggressiveFinalWaitMs = 5000;

        // Defaults are kept here so the automator works even if localization files are not present.
        private static readonly string[] CommonButtonNames = new[]
        {
            "next", "siguiente",
            "install", "instalar",
            "ok", "aceptar", "accept",
            "yes", "si", "sí",
            "continue", "continuar",
            "aceptar y continuar",
            "run", "ejecutar",
            "allow", "permitir",
            "agree", "estar de acuerdo",
            "i agree", "estoy de acuerdo",
            "i accept", "acepto",
            "proceed", "proceder",
            "start", "iniciar",
            "launch", "abrir", "lanzar",
            "retry", "reintentar"
        };

        private static readonly string[] FinalOnlyButtonNames = new[]
        {
            "finish", "done", "close",
            "finalizar", "hecho", "cerrar",
            "complete", "completar",
            "exit", "salir"
        };

        private static readonly string[] AcceptCheckboxTexts = new[]
        {
            "i accept", "i agree",
            "acepto", "acepto los",
            "acepto los terminos", "aceptar",
            "i accept the terms", "i agree to the terms",
            "acepto los términos y condiciones",
            "acepto los acuerdos de licencia",
            "i accept the license agreement",
            "i agree to the license agreement"
        };

        private static readonly string[] NegativeButtonWords = new[]
        {
            "cancel", "cancelar",
            "decline", "rechazar",
            "no", "deny", "denegar",
            "abort", "abortar",
            "exit", "salir",
            "quit", "cerrar"
        };

        private static readonly string[] PasswordFieldWords = new[]
        {
            "password", "contraseña",
            "contrase", "clave",
            "pwd", "passcode",
            "security key", "llave de seguridad"
        };

        private static readonly string[] LicenseFieldWords = new[]
        {
            "license", "licencia",
            "serial", "product key", "productkey",
            "clave del producto",
            "cd key", "cdkey",
            "número de serie", "numero de serie",
            "activation key", "clave de activación",
            "activation code", "código de activación"
        };

        public static void InteractWithProcess(int pid, Action<string> logCallback, CancellationToken token, int timeoutMs = 120000, string? processNameHint = null)
        {
            // Fire-and-forget the async worker; capture the returned Task to avoid analyzer warnings
            _ = InteractWithProcessAsync(pid, logCallback, token, timeoutMs, processNameHint);
        }

        // P/Invoke for SetForegroundWindow and keybd_event as a fallback to send Enter
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static void SendEnterToWindow(Window win)
        {
            if (win == null) throw new ArgumentNullException(nameof(win));

            IntPtr hWnd = IntPtr.Zero;
            try
            {
                var handleProp = win.Properties.NativeWindowHandle;
                if (handleProp != null)
                {
                    var hv = handleProp.Value;
                    if (!hv.Equals(default(nint)))
                    {
                        int h = Convert.ToInt32(hv);
                        hWnd = new IntPtr(h);
                    }
                }
            }
            catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

            if (hWnd == IntPtr.Zero)
            {
                // Try to get top-level window handle via process main window
                try
                {
                    var pidProp = win.Properties.ProcessId;
                    if (pidProp != null)
                    {
                        var pidVal = pidProp.Value;
                        if (pidVal != 0)
                        {
                            var p = Process.GetProcessById((int)pidVal);
                            if (p != null)
                                hWnd = p.MainWindowHandle;
                        }
                    }
                }
                catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
            }

            if (hWnd == IntPtr.Zero)
                throw new InvalidOperationException("Could not determine window handle for SendEnter fallback.");

            // Bring to foreground
            SetForegroundWindow(hWnd);
            Thread.Sleep(100);

            const byte VK_RETURN = 0x0D;
            const uint KEYEVENTF_KEYUP = 0x0002;

            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // Return all descendant process ids (children, grandchildren, ...) for a given PID using WMI
        private static List<int> GetDescendantProcessIds(int rootPid)
        {
            var results = new List<int>();
            try
            {
                var map = new Dictionary<int, int?>();
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            var pidObj = mo["ProcessId"];
                            var ppidObj = mo["ParentProcessId"];
                            if (pidObj == null) continue;
                            int p = Convert.ToInt32(pidObj);
                            int? pp = null;
                            if (ppidObj != null) pp = Convert.ToInt32(ppidObj);
                            map[p] = pp;
                        }
                catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
                    }
                }

                // BFS from rootPid
                var queue = new Queue<int>();
                queue.Enqueue(rootPid);
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var kv in map)
                    {
                        if (kv.Value == cur)
                        {
                            if (!results.Contains(kv.Key))
                            {
                                results.Add(kv.Key);
                                queue.Enqueue(kv.Key);
                            }
                        }
                    }
                }
            }
            catch { }

            return results;
        }

        private static async Task InteractWithProcessAsync(int pid, Action<string> logCallback, CancellationToken token, int timeoutMs, string? processNameHint)
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

                int hostPid = Process.GetCurrentProcess().Id;

                // Maintain per-window watchers so multiple windows can be handled concurrently
                var windowWatchers = new Dictionary<int, CancellationTokenSource>();

                while (!token.IsCancellationRequested && DateTime.UtcNow < end)
                {
                    if (app.HasExited)
                    {
                        logCallback?.Invoke($"[AUTOMATOR] Process {pid} exited.");
                        break;
                    }

                    // Build list of windows for the process and its child PIDs
                    var windowList = new List<Window>();
                        try
                        {
                            var mainWindows = app.GetAllTopLevelWindows(automation);
                            if (mainWindows != null)
                                windowList.AddRange(mainWindows);
                        }
                        catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

                    try
                    {
                        var childPids = GetDescendantProcessIds(pid);
                        foreach (var cp in childPids)
                        {
                            try
                            {
                                var childApp = FlaUI.Core.Application.Attach(cp);
                                var cw = childApp.GetAllTopLevelWindows(automation);
                                if (cw != null)
                                    windowList.AddRange(cw);
                            }
                            catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
                        }
                    }
                    catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

                    // For each window, ensure a watcher task is running
                    foreach (var win in windowList)
                    {
                        if (win == null) continue;
                        int winHandle = 0;
                        try
                        {
                            // Try to read native window handle safely
                            try
                            {
                                var hv = win.Properties.NativeWindowHandle.Value;
                                winHandle = Convert.ToInt32(hv);
                            }
                            catch (Exception ex) { try { Logger.WriteException(ex); } catch { } ;
                                // cannot get handle -> skip
                                continue;
                            }
                        }
                        catch (Exception ex) { try { Logger.WriteException(ex); } catch { } ; continue; }
                        if (winHandle == 0) continue;

                        if (!windowWatchers.ContainsKey(winHandle))
                        {
                            var wcts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            windowWatchers[winHandle] = wcts;
                            // Capture the window reference
                            var capturedWin = win;
                            Task.Run(async () =>
                            {
                                try
                                {
                                    while (!wcts.Token.IsCancellationRequested)
                                    {
                                        try
                                        {
                                            // If window closed or offscreen, break
                                            try
                                            {
                                                var isOffObj = capturedWin.Properties.IsOffscreen?.Value;
                                                if (isOffObj == true)
                                                    break;
                                            }
                                            catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

                                            // Process controls only within this window
                                            bool acted = false;

                                            // 0) Detect required input fields (password/license). If present and empty, pause this watcher
                                            try
                                            {
                                                var edits = capturedWin.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                                                bool needsInput = false;
                                                AutomationElement? neededEdit = null;
                                                foreach (var ed in edits)
                                                {
                                                        try
                                                        {
                                                            var ename = ed.Name ?? string.Empty;
                                                            var labeledBy = ed.Properties.LabeledBy?.Value;
                                                            var labelName = (labeledBy as AutomationElement)?.Name ?? string.Empty;

                                                            string combined = (ename + " " + labelName).ToLowerInvariant();

                                                            if (PasswordFieldWords.Any(w => combined.Contains(w)) || LicenseFieldWords.Any(w => combined.Contains(w)))
                                                            {
                                                                // read value if available
                                                                string val = string.Empty;
                                                                try
                                                                {
                                                                    val = ed.Patterns.Value.PatternOrDefault?.Value ?? ed.AsTextBox()?.Text ?? string.Empty;
                                                                }
                                                                catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

                                                                if (string.IsNullOrWhiteSpace(val))
                                                                {
                                                                    needsInput = true;
                                                                    neededEdit = ed;
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
                                                }

                                                if (needsInput && neededEdit != null)
                                                {
                                                    logCallback?.Invoke($"[AUTOMATOR] Waiting for user input for field related to '{neededEdit.Name}' in window {winHandle}");

                                                    // Snapshot current buttons and title to detect user click/change later
                                                    var beforeButtons = capturedWin.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Select(x => x.Name ?? string.Empty).ToArray();
                                                    var beforeTitle = capturedWin.AsWindow()?.Title ?? string.Empty;

                                                    // Wait until the edit receives a value or the window changes (user clicked)
                                                    bool resumed = false;
                                                    while (!wcts.Token.IsCancellationRequested)
                                                    {
                                                        try
                                                        {
                                                            // Check if window closed/offscreen
                                                            try { if (capturedWin.Properties.IsOffscreen?.Value == true) break; } catch { }

                                                            // Re-check value
                                                            string val2 = string.Empty;
                                                            try { val2 = neededEdit.Patterns.Value.PatternOrDefault?.Value ?? neededEdit.AsTextBox()?.Text ?? string.Empty; } catch { }
                                                            if (!string.IsNullOrWhiteSpace(val2))
                                                            {
                                                                // Now wait for user to click/advance: detect change in button set or title or control tree
                                                                for (int i = 0; i < 120 && !wcts.Token.IsCancellationRequested; i++) // wait up to ~120s for user click/change
                                                                {
                                                                    try
                                                                    {
                                                                        var afterButtons = capturedWin.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Select(x => x.Name ?? string.Empty).ToArray();
                                                                        var afterTitle = capturedWin.AsWindow()?.Title ?? string.Empty;
                                                                        if (afterTitle != beforeTitle || afterButtons.Length != beforeButtons.Length || !afterButtons.SequenceEqual(beforeButtons))
                                                                        {
                                                                            resumed = true;
                                                                            break;
                                                                        }
                                                                    }
                                                                    catch { }
                                                                    await Task.Delay(1000, wcts.Token).ConfigureAwait(false);
                                                                }

                                                                if (resumed) break;
                                                            }
                                                        }
                                                        catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

                                                        await Task.Delay(1000, wcts.Token).ConfigureAwait(false);
                                                    }

                                                    logCallback?.Invoke($"[AUTOMATOR] Resuming watcher for window {winHandle}");
                                                }
                                            }
                                            catch { }

                                            try
                                            {
                                                // Check checkboxes
                                                var cbs = capturedWin.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                                                foreach (var cb in cbs)
                                                {
                                                    if (AcceptCheckboxTexts.Any(t => (cb.Name ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)))
                                                    {
                                                        try
                                                        {
                                                            var box = cb.AsCheckBox();
                                                            if (box != null && (box.IsChecked == false || box.IsChecked == null))
                                                            {
                                                                box.Toggle();
                                                                logCallback?.Invoke($"[AUTOMATOR] Checkbox toggled (win {winHandle}): {cb.Name}");
                                                                acted = true;
                                                            }
                                                        }
                                                        catch { }
                                                    }
                                                }

                                                // Check buttons
                                                var btns = capturedWin.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                                                foreach (var b in btns)
                                                {
                                                    try
                                                    {
                                                        var name = b.Name ?? "";
                                                        var nameNorm = (name ?? string.Empty).Trim();
                                                        if (!CommonButtonNames.Any(t => System.Text.RegularExpressions.Regex.IsMatch(name ?? string.Empty, $"\\b{System.Text.RegularExpressions.Regex.Escape(t)}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                                                            continue;
                                                        if (NegativeButtonWords.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                                                            continue;
                                                        // If this is a final-only button (finish/done/close), only act on it
                                                        // when there are no other non-negative, non-final buttons available in the same window.
                                                        bool isFinalOnly = FinalOnlyButtonNames.Any(fn => System.Text.RegularExpressions.Regex.IsMatch(nameNorm, $"\\b{System.Text.RegularExpressions.Regex.Escape(fn)}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                                                        if (isFinalOnly)
                                                        {
                                                            // determine if other actionable buttons exist
                                                            bool otherActionableExists()
                                                            {
                                                                try
                                                                {
                                                                    foreach (var ob in capturedWin.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                                                                    {
                                                                        try
                                                                        {
                                                                            var on = ob.Name ?? string.Empty;
                                                                            if (string.IsNullOrWhiteSpace(on)) continue;
                                                                            // skip negatives
                                                                            if (NegativeButtonWords.Any(n => on.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                                                                            // if other final-only, ignore
                                                                            if (FinalOnlyButtonNames.Any(fn => System.Text.RegularExpressions.Regex.IsMatch(on, $"\\b{System.Text.RegularExpressions.Regex.Escape(fn)}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) continue;
                                                                            // if matches the common names (and is enabled) then it's another actionable option
                                                                            if (CommonButtonNames.Any(t => System.Text.RegularExpressions.Regex.IsMatch(on, $"\\b{System.Text.RegularExpressions.Regex.Escape(t)}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                                                                            {
                                                                                try { var obBtn = ob.AsButton(); if (obBtn != null && obBtn.IsEnabled) { return true; } } catch { }
                                                                            }
                                                                        }
                                                                        catch { }
                                                                    }
                                                                }
                                                                catch { }

                                                                return false;
                                                            }

                                                            if (otherActionableExists())
                                                            {
                                                                if (AggressiveFinalClick)
                                                                {
                                                                    // Wait up to AggressiveFinalWaitMs for other actionables to disappear or become disabled
                                                                    var waitUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(AggressiveFinalWaitMs);
                                                                    while (DateTime.UtcNow < waitUntil && otherActionableExists() && !wcts.Token.IsCancellationRequested)
                                                                    {
                                                                        await Task.Delay(400, wcts.Token).ConfigureAwait(false);
                                                                    }

                                                                    // If still exists after wait, proceed anyway (force final click)
                                                                    // otherwise fall through to press the final-only button normally
                                                                }
                                                                else
                                                                {
                                                                    // skip pressing Finish/Done/Close if there are other options and not in aggressive mode
                                                                    continue;
                                                                }
                                                            }
                                                        }

                                                        var btn = b.AsButton();
                                                        if (btn != null && btn.IsEnabled)
                                                        {
                                                            try
                                                            {
                                                                btn.Invoke();
                                                                logCallback?.Invoke($"[AUTOMATOR] Invoke() → {name} (win {winHandle})");
                                                                acted = true;
                                                            }
                                                            catch
                                                            {
                                                                try { capturedWin.AsWindow()?.Focus(); } catch { }
                                                                try
                                                                {
                                                                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                                                                    logCallback?.Invoke($"[AUTOMATOR] Sent Enter (fallback) → {name} (win {winHandle})");
                                                                    acted = true;
                                                                }
                                                                catch
                                                                {
                                                                    try { SendEnterToWindow(capturedWin); logCallback?.Invoke($"[AUTOMATOR] Sent Enter via SendInput → {name} (win {winHandle})"); acted = true; } catch { }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
                                                }
                                            }
                                                        catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }

                                            if (acted)
                                                await Task.Delay(700);
                                            else
                                                await Task.Delay(400);
                                        }
                                        catch { break; }
                                    }
                                }
                                finally
                                {
                                    try { windowWatchers.Remove(winHandle); } catch { }
                                }
                            }, wcts.Token);
                        }
                    }

                    // Small delay before next enumeration
                    await Task.Delay(500);
                }

                // Cancel any remaining watchers
                foreach (var kv in windowWatchers)
                {
                    try { kv.Value.Cancel(); } catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
                }

                logCallback?.Invoke($"[AUTOMATOR] Timeout reached for PID {pid}");
            }
                                                    catch (Exception ex) { try { Logger.WriteException(ex); } catch { } }
        }
    }
}
