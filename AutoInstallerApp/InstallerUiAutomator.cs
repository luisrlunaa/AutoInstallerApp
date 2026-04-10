using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoInstallerApp
{
    public static class InstallerUiAutomator
    {
        public static bool ForceNoCursorInteraction { get; set; } = false;
        public static bool AggressiveFinalClick = true;
        public static int AggressiveFinalWaitMs = 5000;
        // Exponer PerWindowTimeoutMs para que otros módulos (InstallerService) lo referencien
        public static int PerWindowTimeoutMs { get; set; } = 180000; // 3 minutes per window watcher

        private static readonly string[] AcceptCheckboxTexts = new[] {
            "i accept", "i agree", "acepto", "acepto los", "acepto los terminos", "aceptar",
            "i accept the terms", "i agree to the terms", "acepto los términos y condiciones",
            "acepto los acuerdos de licencia", "i accept the license agreement", "i agree to the license agreement"
        };

        private static readonly string[] NegativeButtonWords = new[] {
            "cancel", "cancelar", "decline", "rechazar", "no", "not", "deny", "denegar",
            "abort", "abortar", "exit", "salir", "quit", "cerrar", "don't", "do not"
        };

        private static readonly string[] PositiveButtonRegex = new[] {
            "next", "install", "accept", "agree", "continue", "ok", "yes", "run", "finish", "done", "complete", "apply"
        };

        private static readonly string[] LicenseFieldWords = new[] {
            "license", "licencia", "serial", "product key", "productkey", "clave del producto",
            "cd key", "cdkey", "número de serie", "numero de serie", "activation key",
            "clave de activación", "activation code", "código de activación", "site key", "sitekey", "auth key"
        };

        // Watchers keyed by a stable string id (runtime id or fallback)
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> WindowWatchers = new();

        public static void CancelWatchersForProcess(int pid)
        {
            try
            {
                var keys = WindowWatchers.Keys.ToArray();
                foreach (var key in keys)
                {
                    if (key.StartsWith($"pid:{pid}:") || key.Contains($":pid={pid}:") || key.Contains($";pid={pid};"))
                    {
                        if (WindowWatchers.TryRemove(key, out var cts))
                        {
                            try { cts.Cancel(); } catch { }
                            try { cts.Dispose(); } catch { }
                            Logger.Write($"[AUTOMATOR] Cancelled watcher key={key}, pid={pid}");
                        }
                    }
                }
            }
            catch { }
        }

        public static void InteractWithProcess(int pid, Action<string> logCallback, CancellationToken cancellationToken, int timeoutMs = 180000, string? processNameHint = null)
        {
            try
            {
                using (var automation = new UIA3Automation())
                {
                    Stopwatch swTotal = Stopwatch.StartNew();
                    var seenWindowKeys = new HashSet<string>();

                    logCallback?.Invoke($"[AUTOMATOR] InteractWithProcess started for PID={pid}, hint={processNameHint}");

                    while (!cancellationToken.IsCancellationRequested && swTotal.ElapsedMilliseconds < timeoutMs)
                    {
                        try
                        {
                            var desktop = automation.GetDesktop();
                            var wins = desktop.FindAllChildren(cf => cf.ByProcessId(pid).And(cf.ByControlType(ControlType.Window)));

                            logCallback?.Invoke($"[AUTOMATOR] Found {wins.Length} top-level window(s) for PID={pid}");

                            foreach (var w in wins)
                            {
                                try
                                {
                                    var win = w.AsWindow();
                                    if (win == null) continue;

                                    string key = BuildWindowKey(win, pid);
                                    string title = SafeGetWindowTitle(win);

                                    logCallback?.Invoke($"[AUTOMATOR] Window candidate Title='{title}' Key='{key}'");

                                    if (seenWindowKeys.Contains(key)) continue;
                                    seenWindowKeys.Add(key);

                                    logCallback?.Invoke($"[AUTOMATOR] Launching watcher for window key={key}, title='{title}'");
                                    _ = WatchWindowAsync(win, pid, key, logCallback, cancellationToken);
                                }
                                catch (Exception ex) { Logger.WriteException(ex, "InteractWithProcess:perWindow"); }
                            }
                        }
                        catch (Exception ex) { Logger.WriteException(ex, "InteractWithProcess:mainLoop"); }

                        Thread.Sleep(600);
                    }

                    logCallback?.Invoke($"[AUTOMATOR] InteractWithProcess finished for PID={pid}");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteException(ex, "InteractWithProcess:outer");
            }
        }

        private static string BuildWindowKey(Window win, int pid)
        {
            try
            {
                // RuntimeId handling (unchanged)
                var ridProp = win.Properties.RuntimeId;
                if (ridProp != null)
                {
                    try
                    {
                        var rid = ridProp.Value;
                        if (rid is int[] arr && arr.Length > 0)
                        {
                            return $"pid:{pid}:rid:{string.Join("-", arr)}";
                        }
                        else if (rid != null)
                        {
                            return $"pid:{pid}:rid:{rid.ToString()}";
                        }
                    }
                    catch { }
                }

                // NativeWindowHandle: avoid using ?. on nint; handle safely and convert to int
                try
                {
                    var nativeHandleProp = win.Properties.NativeWindowHandle;
                    object? hvObj = null;
                    if (nativeHandleProp != null)
                    {
                        try { hvObj = nativeHandleProp.Value; } catch { hvObj = null; }
                    }

                    if (hvObj != null)
                    {
                        try
                        {
                            // hvObj can be nint, int, long, etc.
                            int handle = 0;
                            if (hvObj is nint nval)
                            {
                                handle = (int)nval;
                            }
                            else if (hvObj is int ival)
                            {
                                handle = ival;
                            }
                            else if (hvObj is long lval)
                            {
                                handle = Convert.ToInt32(lval);
                            }
                            else
                            {
                                handle = Convert.ToInt32(hvObj);
                            }

                            if (handle != 0)
                                return $"pid:{pid};hwnd={handle}";
                        }
                        catch { /* conversion failed - continue to fallback */ }
                    }
                }
                catch { /* ignore and continue to fallback */ }

                string title = SafeGetWindowTitle(win);
                string fallback = $"pid:{pid}:titlehash:{Math.Abs((title + DateTime.UtcNow.Ticks).GetHashCode())}";
                return fallback;
            }
            catch { return $"pid:{pid}:unknown:{Guid.NewGuid()}"; }
        }

        private static Task WatchWindowAsync(Window win, int procId, string key, Action<string>? log, CancellationToken globalToken)
        {
            return Task.Run(async () =>
            {
                if (string.IsNullOrEmpty(key)) key = $"pid:{procId}:key:{Guid.NewGuid()}";

                var cts = new CancellationTokenSource();
                if (!WindowWatchers.TryAdd(key, cts))
                {
                    try { cts.Dispose(); } catch { }
                    return;
                }

                var token = cts.Token;
                try
                {
                    log?.Invoke($"[AUTOMATOR-WATCHER] Started watcher key={key}, PID={procId}, Title='{SafeGetWindowTitle(win)}'");
                    var sw = Stopwatch.StartNew();
                    int idleCycles = 0;

                    while (!token.IsCancellationRequested && !globalToken.IsCancellationRequested && sw.ElapsedMilliseconds < PerWindowTimeoutMs)
                    {
                        try
                        {
                            bool isAvailable = true;
                            try { isAvailable = win.IsAvailable; } catch { isAvailable = true; }
                            if (!isAvailable)
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] Window key={key} no longer available, exiting watcher.");
                                break;
                            }

                            // 1) If a cancel-confirmation dialog appears, handle it immediately
                            if (HandleCancelConfirmation(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] Handled cancel-confirmation for key={key}");
                                idleCycles = 0;
                                await Task.Delay(300, token).ConfigureAwait(false);
                                continue;
                            }

                            // 2) Check and mark agreement checkboxes
                            TryCheckAgreementCheckboxes(win, log);

                            // 3) TightVNC autofill
                            if (TryAutoFillTightVnc(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] TightVNC autofill applied for key={key}");
                                idleCycles = 0;
                            }

                            // 4) Agent Shell Setup / Spiceworks autofill
                            if (TryAutoFillAgentShell(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] Agent Shell autofill applied for key={key}");
                                idleCycles = 0;
                            }

                            // 5) License/sitekey autofill
                            if (TryAutoFillLicense(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] License/sitekey autofill applied for key={key}");
                                idleCycles = 0;
                            }

                            // 6) Sophos special handling
                            if (TryHandleSophos(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] Sophos handler applied for key={key}");
                                idleCycles = 0;
                            }

                            // 7) Web bootstrapper heuristics
                            if (TryAutomateWebBootstrapper(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] Web bootstrapper heuristics applied for key={key}");
                                idleCycles = 0;
                            }

                            // 8) Try to invoke positive buttons (Next/Install/Accept/Yes)
                            if (TryInvokePositiveButton(win, log))
                            {
                                log?.Invoke($"[AUTOMATOR-WATCHER] Positive button invoked for key={key}");
                                idleCycles = 0;
                            }
                            else
                            {
                                idleCycles++;
                                if (idleCycles > 8)
                                {
                                    try { SaveUIASnapshot(win, $"idle_{SanitizeKey(key)}"); } catch { }
                                    idleCycles = 0;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteException(ex, "WatchWindowAsync:loop");
                        }

                        await Task.Delay(600, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteException(ex, "WatchWindowAsync:outer");
                }
                finally
                {
                    WindowWatchers.TryRemove(key, out var _);
                    try { cts.Cancel(); } catch { }
                    try { cts.Dispose(); } catch { }
                    log?.Invoke($"[AUTOMATOR-WATCHER] Stopped watcher key={key}");
                }
            }, CancellationToken.None);
        }

        private static string SanitizeKey(string key)
        {
            try { return key.Replace(':', '_').Replace(';', '_').Replace(' ', '_'); } catch { return key; }
        }

        private static string SafeGetWindowTitle(Window win)
        {
            try { return win.Title ?? "(no title)"; } catch { return "(no title)"; }
        }

        private static bool HandleCancelConfirmation(Window win, Action<string>? log)
        {
            try
            {
                string title = SafeGetWindowTitle(win).ToLowerInvariant();
                var textElems = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
                string combinedText = string.Join(" ", textElems.Select(t => (t.Name ?? "").ToLowerInvariant()));

                if (title.Contains("cancel") || title.Contains("are you sure") ||
                    combinedText.Contains("are you sure you want to cancel") ||
                    combinedText.Contains("confirm cancel") || combinedText.Contains("do you want to cancel"))
                {
                    log?.Invoke($"[AUTOMATOR] Detected cancel-confirm dialog (title='{title}'). Attempting to press 'No'.");

                    var buttons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                    // Prefer explicit "No"
                    foreach (var b in buttons)
                    {
                        try
                        {
                            var name = (b.Name ?? string.Empty).Trim().ToLowerInvariant();
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (name == "no" || name == "don't" || name == "do not" || name.Contains("no"))
                            {
                                try { b.AsButton()?.Invoke(); log?.Invoke($"[AUTOMATOR] Invoked button '{b.Name}' (No)."); return true; } catch { }
                            }
                        }
                        catch { }
                    }

                    // Fallback: invoke any button that is not Cancel/Yes (prefer keep/continue)
                    foreach (var b in buttons)
                    {
                        try
                        {
                            var name = (b.Name ?? string.Empty).Trim().ToLowerInvariant();
                            if (name.Contains("cancel")) continue;
                            var btn = b.AsButton();
                            if (btn != null && btn.IsEnabled)
                            {
                                try { btn.Invoke(); log?.Invoke($"[AUTOMATOR] Fallback invoked button '{b.Name}' to avoid cancel."); return true; } catch { }
                            }
                        }
                        catch { }
                    }

                    // Last resort: send Escape to dismiss (often equivalent to No)
                    try
                    {
                        win.Focus();
                        Thread.Sleep(80);
                        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
                        log?.Invoke("[AUTOMATOR] Sent Escape as last-resort to dismiss cancel dialog.");
                        return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static void TryCheckAgreementCheckboxes(Window win, Action<string>? log)
        {
            try
            {
                var cbs = win.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                foreach (var cb in cbs)
                {
                    try
                    {
                        var name = (cb.Name ?? string.Empty).ToLowerInvariant();
                        if (AcceptCheckboxTexts.Any(t => name.Contains(t)))
                        {
                            var box = cb.AsCheckBox();
                            if (box != null && (box.IsChecked == false || box.IsChecked == null))
                            {
                                box.IsChecked = true;
                                log?.Invoke($"[AUTOMATOR] Checked agreement checkbox '{cb.Name}' on window '{SafeGetWindowTitle(win)}'");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool TryAutoFillTightVnc(Window win, Action<string>? logCallback)
        {
            try
            {
                var title = SafeGetWindowTitle(win);
                var currentFile = InstallerService.CurrentFile ?? string.Empty;
                if (!(title.IndexOf("tightvnc", StringComparison.OrdinalIgnoreCase) >= 0 || currentFile.IndexOf("tightvnc", StringComparison.OrdinalIgnoreCase) >= 0))
                    return false;

                logCallback?.Invoke("[AUTOMATOR] TightVNC Password screen detected. Attempting autofill...");

                var edits = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                if (edits != null && edits.Length > 0)
                {
                    int filled = 0;
                    foreach (var ed in edits)
                    {
                        try
                        {
                            var tb = ed.AsTextBox();
                            if (tb != null && tb.Patterns.Value.IsSupported)
                            {
                                tb.Text = "aica";
                                filled++;
                                if (filled >= 4) break;
                            }
                        }
                        catch { }
                    }
                    if (filled > 0)
                    {
                        try { FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN); } catch { }
                        logCallback?.Invoke("[AUTOMATOR] Auto-filled TightVNC password fields via UIA.");
                        return true;
                    }
                }

                try
                {
                    var password = "aica";
                    Thread t = new Thread(() => System.Windows.Forms.Clipboard.SetText(password));
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    t.Join();

                    if (edits != null && edits.Length > 0)
                    {
                        foreach (var ed in edits)
                        {
                            try
                            {
                                ed.Focus();
                                Thread.Sleep(80);
                                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
                                FlaUI.Core.Input.Keyboard.Type('v');
                                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
                                Thread.Sleep(120);
                            }
                            catch { }
                        }
                        try { FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN); } catch { }
                        logCallback?.Invoke("[AUTOMATOR] Auto-filled TightVNC passwords via Clipboard/Ctrl+V.");
                        return true;
                    }
                }
                catch { }

                return false;
            }
            catch { return false; }
        }

        private static bool TryAutoFillAgentShell(Window win, Action<string>? logCallback)
        {
            try
            {
                var title = SafeGetWindowTitle(win);
                var currentFile = InstallerService.CurrentFile ?? string.Empty;

                // Verificar si es la ventana correcta
                if (!(title.IndexOf("Agent Shell Setup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      title.IndexOf("Spiceworks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      currentFile.IndexOf("Agent Shell", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return false;
                }

                bool actionTaken = false;

                // 1. Obtener y seleccionar los RadioButtons
                var radioButtons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton));
                if (radioButtons.Length >= 6)
                {
                    // Seleccionar el 3ro de la primera sección (índice 2)
                    var rb1 = radioButtons[2].AsRadioButton();
                    if (rb1 != null && (rb1.IsChecked == false || rb1.IsChecked == null))
                    {
                        rb1.Click();
                        logCallback?.Invoke("[AUTOMATOR-AGENTSHELL] Selected 1st radio button (index 2).");
                        Thread.Sleep(300); // Pequeña pausa para que la UI habilite las casillas
                        actionTaken = true;
                    }

                    // Seleccionar el 3ro de la segunda sección (índice 5)
                    var rb2 = radioButtons[5].AsRadioButton();
                    if (rb2 != null && (rb2.IsChecked == false || rb2.IsChecked == null))
                    {
                        rb2.Click();
                        logCallback?.Invoke("[AUTOMATOR-AGENTSHELL] Selected 2nd radio button (index 5).");
                        Thread.Sleep(300);
                        actionTaken = true;
                    }
                }

                // 2. Llenar las 4 casillas de texto habilitadas
                var editBoxes = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                int filledCount = 0;
                string password = "aica";

                foreach (var element in editBoxes)
                {
                    var edit = element.AsTextBox();
                    // Solo interactuar con las casillas que están habilitadas y no son de solo lectura
                    if (edit != null && edit.IsEnabled && !edit.IsReadOnly)
                    {
                        if (edit.Text != password)
                        {
                            edit.Text = password;
                            actionTaken = true;
                        }
                        filledCount++;
                        if (filledCount >= 4) break; // Detenerse al llenar las 4 requeridas
                    }
                }

                if (actionTaken)
                {
                    logCallback?.Invoke($"[AUTOMATOR-AGENTSHELL] Auto-filled fields. Passwords filled: {filledCount}");
                }

                return actionTaken;
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "TryAutoFillAgentShell"); } catch { }
                return false;
            }
        }

        private static bool TryAutoFillLicense(Window win, Action<string>? logCallback)
        {
            try
            {
                var installerPath = InstallerService.CurrentFile;
                var siteKeyPath = FindSiteKeyNearInstaller(installerPath);
                if (siteKeyPath == null)
                {
                    var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                    if (File.Exists(tmpSk)) siteKeyPath = tmpSk;
                }

                if (siteKeyPath == null) return false;

                string keyText = File.ReadAllText(siteKeyPath).Trim();
                if (string.IsNullOrEmpty(keyText)) return false;

                var edits = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                foreach (var ed in edits)
                {
                    try
                    {
                        var ename = (ed.Name ?? string.Empty).ToLowerInvariant();
                        var labelName = (ed.Properties.LabeledBy?.Value as AutomationElement)?.Name?.ToLowerInvariant() ?? "";

                        if (LicenseFieldWords.Any(w => ename.Contains(w) || labelName.Contains(w)))
                        {
                            var tb = ed.AsTextBox();
                            if (tb != null && tb.Patterns.Value.IsSupported)
                            {
                                tb.Text = keyText;
                                logCallback?.Invoke($"[AUTOMATOR] Auto-filled license via UIA into field '{ed.Name}'");
                                return true;
                            }
                            else
                            {
                                ed.Focus();
                                Thread.Sleep(100);

                                Thread t = new Thread(() => System.Windows.Forms.Clipboard.SetText(keyText));
                                t.SetApartmentState(ApartmentState.STA);
                                t.Start();
                                t.Join();

                                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
                                FlaUI.Core.Input.Keyboard.Type('v');
                                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);

                                logCallback?.Invoke($"[AUTOMATOR] Auto-filled license via Clipboard/Ctrl+V into field '{ed.Name}'");
                                return true;
                            }
                        }
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }

        private static bool TryHandleSophos(Window win, Action<string>? log)
        {
            try
            {
                var title = SafeGetWindowTitle(win);
                var curFile = InstallerService.CurrentFile ?? string.Empty;
                if (!(title.IndexOf("sophos", StringComparison.OrdinalIgnoreCase) >= 0 || curFile.IndexOf("sophos", StringComparison.OrdinalIgnoreCase) >= 0))
                    return false;

                log?.Invoke($"[AUTOMATOR-SOPHOS] Handling window '{SafeGetWindowTitle(win)}'");

                try
                {
                    var cbs = win.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                    foreach (var cb in cbs)
                    {
                        try
                        {
                            var name = cb.Name ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (AcceptCheckboxTexts.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) || name.IndexOf("agree", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var box = cb.AsCheckBox();
                                if (box != null && (box.IsChecked == false || box.IsChecked == null))
                                {
                                    box.IsChecked = true;
                                    log?.Invoke("[AUTOMATOR-SOPHOS] Checked agreement checkbox");
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                var buttons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                foreach (var b in buttons)
                {
                    try
                    {
                        var name = (b.Name ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (NegativeButtonWords.Any(nb => name.IndexOf(nb, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;

                        if (System.Text.RegularExpressions.Regex.IsMatch(name, "(continue|install|allow|accept|run|ok|next)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            try
                            {
                                var btn = b.AsButton();
                                if (btn != null && btn.IsEnabled)
                                {
                                    try { btn.Invoke(); log?.Invoke($"[AUTOMATOR-SOPHOS] Invoked button '{name}'"); return true; } catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                try
                {
                    win.Focus();
                }
                catch { }

                return false;
            }
            catch { return false; }
        }

        private static bool TryAutomateWebBootstrapper(Window win, Action<string>? log)
        {
            try
            {
                // Heuristics for web bootstrappers (Edge/IE/Chromium dialogs embedded)
                var title = SafeGetWindowTitle(win);
                if (string.IsNullOrEmpty(title)) return false;

                if (title.IndexOf("security", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("open file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("windows protected", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        // Prefer Enter to accept
                        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                        return true;
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }

        private static bool TryInvokePositiveButton(Window win, Action<string>? log)
        {
            try
            {
                var buttons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                foreach (var b in buttons)
                {
                    try
                    {
                        var name = (b.Name ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Skip negative words
                        if (NegativeButtonWords.Any(nb => name.IndexOf(nb, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;

                        // If matches positive regex, try to invoke
                        if (PositiveButtonRegex.Any(p => name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            var btn = b.AsButton();
                            if (btn != null && btn.IsEnabled)
                            {
                                try { btn.Invoke(); return true; } catch { }
                            }
                        }
                    }
                    catch { }
                }

                // Fallback: try pressing Enter (safer than Space)
                try
                {
                    win.Focus();
                    Thread.Sleep(60);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                    return true;
                }
                catch { }

                return false;
            }
            catch { return false; }
        }

        // Helper: save UIA snapshot for debugging
        private static void SaveUIASnapshot(Window win, string name)
        {
            try
            {
                var dump = win.ToString();
                var path = Path.Combine(Path.GetTempPath(), $"uia_snapshot_{name}_{DateTime.Now:yyyyMMddHHmmss}.txt");
                File.WriteAllText(path, dump);
            }
            catch { }
        }

        // Helper: find sitekey near installer (private)
        private static string? FindSiteKeyNearInstaller(string? installerPath)
        {
            try
            {
                if (string.IsNullOrEmpty(installerPath)) return null;
                var dir = Path.GetDirectoryName(installerPath);
                if (dir == null) return null;

                var candidates = new List<string>
                {
                    Path.Combine(dir, "sitekey"),
                    Path.Combine(dir, "sitekey.txt")
                };

                var parent = Directory.GetParent(dir);
                if (parent != null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "sitekey"));
                    candidates.Add(Path.Combine(parent.FullName, "sitekey.txt"));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }
    }
}
