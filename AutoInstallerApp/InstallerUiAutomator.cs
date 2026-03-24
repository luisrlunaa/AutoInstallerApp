using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        // Maximum time to wait for automation to complete per window
        public static int PerWindowTimeoutMs = 30000;

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

        // UPDATED: Added more negative words
        private static readonly string[] NegativeButtonWords = new[]
        {
            "cancel", "cancelar",
            "decline", "rechazar",
            "no", "not", "deny", "denegar",
            "abort", "abortar",
            "exit", "salir",
            "quit", "cerrar",
            "don't", "do not"
        };

        private static readonly string[] PasswordFieldWords = new[]
        {
            "password", "contraseña",
            "contrase", "clave",
            "pwd", "passcode",
            "security key", "llave de seguridad"
        };

        // UPDATED: Added Spiceworks-related sitekey terms
        private static readonly string[] LicenseFieldWords = new[]
        {
            "license", "licencia",
            "serial", "product key", "productkey",
            "clave del producto",
            "cd key", "cdkey",
            "número de serie", "numero de serie",
            "activation key", "clave de activación",
            "activation code", "código de activación",
            "site key", "sitekey", "auth key"
        };

        // Attempt to resolve a .lnk shortcut to its target path using WScript.Shell COM
        private static string? ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;
                Type? wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null) return null;
                dynamic? shell = Activator.CreateInstance(wshType);
                if (shell == null) return null;
                dynamic lnk = shell.CreateShortcut(lnkPath);
                string target = lnk.TargetPath as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target)) return null;
                return target;
            }
            catch { return null; }
        }

        // Safely check IsOffscreen property without throwing if property unsupported
        private static bool IsWindowOffscreenSafe(Window win)
        {
            if (win == null) return false;
            try
            {
                var prop = win.Properties.IsOffscreen;
                if (prop == null) return false;
                // Access Value inside try/catch because some providers may not support it
                try
                {
                    var v = prop.Value;
                    return v == true;
                }
                catch { return false; }
            }
            catch { return false; }
        }

        // Try to find a 'sitekey' file near the installer path. Search installer dir, parent dirs and any resolved .lnk targets.
        private static string? FindSiteKeyNearInstaller(string? installerPath)
        {
            try
            {
                if (string.IsNullOrEmpty(installerPath)) return null;
                var dir = Path.GetDirectoryName(installerPath);
                if (dir == null) return null;

                // Check current dir and up to two parent levels
                var candidates = new List<string>();
                candidates.Add(Path.Combine(dir, "sitekey"));
                candidates.Add(Path.Combine(dir, "sitekey.txt"));

                var parent = Directory.GetParent(dir);
                if (parent != null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "sitekey"));
                    candidates.Add(Path.Combine(parent.FullName, "sitekey.txt"));
                }

                var parent2 = parent?.Parent;
                if (parent2 != null)
                {
                    candidates.Add(Path.Combine(parent2.FullName, "sitekey"));
                    candidates.Add(Path.Combine(parent2.FullName, "sitekey.txt"));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }

                // Also check for .lnk files in dir and parent that might point to the real install location
                var lnkFiles = Directory.GetFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly).ToList();
                if (parent != null) lnkFiles.AddRange(Directory.GetFiles(parent.FullName, "*.lnk", SearchOption.TopDirectoryOnly));

                foreach (var lnk in lnkFiles)
                {
                    var target = ResolveShortcutTarget(lnk);
                    if (string.IsNullOrEmpty(target)) continue;
                    string targetDir = Path.GetDirectoryName(target) ?? target;
                    var candidate1 = Path.Combine(targetDir, "sitekey");
                    var candidate2 = Path.Combine(targetDir, "sitekey.txt");
                    if (File.Exists(candidate1)) return candidate1;
                    if (File.Exists(candidate2)) return candidate2;
                }

                return null;
            }
            catch { return null; }
        }

        // NEW: Publish the found sitekey into %TEMP%\auto_installer_sitekey.txt so AHK and other fallbacks can use it.
        // Returns the path to the temp file if published, otherwise null.
        private static string? PublishSiteKeyToTemp(string? installerPath, Action<string>? logCallback)
        {
            try
            {
                var siteKeyPath = FindSiteKeyNearInstaller(installerPath);
                if (siteKeyPath == null)
                {
                    // Also check if InstallerService already published a temp sitekey
                    var tmpSk = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                    if (File.Exists(tmpSk))
                    {
                        logCallback?.Invoke($"[AUTOMATOR] Temp sitekey already present at {tmpSk}");
                        return tmpSk;
                    }

                    logCallback?.Invoke("[AUTOMATOR] No sitekey file found near installer.");
                    return null;
                }

                string keyText = File.ReadAllText(siteKeyPath).Trim();
                if (string.IsNullOrEmpty(keyText))
                {
                    logCallback?.Invoke($"[AUTOMATOR] Found sitekey file at {siteKeyPath} but it is empty.");
                    return null;
                }

                var dest = Path.Combine(Path.GetTempPath(), "auto_installer_sitekey.txt");
                try
                {
                    File.WriteAllText(dest, keyText);
                    logCallback?.Invoke($"[AUTOMATOR] Published sitekey to temp: {dest}");
                    return dest;
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[AUTOMATOR] Failed to publish sitekey to temp: {ex.Message}");
                    try { Logger.WriteException(ex, "PublishSiteKeyToTemp"); } catch { }
                    return null;
                }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "PublishSiteKeyToTemp"); } catch { }
                return null;
            }
        }

        // UPDATED: Try to auto-fill TightVNC password fields using UI elements or Keyboard fallback
        private static bool TryAutoFillTightVnc(Window win, Action<string>? logCallback)
        {
            try
            {
                var title = win.AsWindow()?.Title ?? string.Empty;
                var currentFile = InstallerService.CurrentFile ?? string.Empty;
                if (!(title.IndexOf("tightvnc", StringComparison.OrdinalIgnoreCase) >= 0 || currentFile.IndexOf("tightvnc", StringComparison.OrdinalIgnoreCase) >= 0))
                    return false;

                // NEW LOGIC: check if it's the specific password window to inject keystrokes
                if (title.Contains("TightVNC Server") || title.Contains("Password") || title.Contains("TightVNC"))
                {
                    logCallback?.Invoke("[AUTOMATOR] TightVNC Password screen detected. Using Keyboard injection...");
                    try { win.Focus(); } catch { }
                    Thread.Sleep(200);

                    // Prefer UIA set value if possible
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
                            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                            logCallback?.Invoke("[AUTOMATOR] Auto-filled TightVNC password fields via UIA.");
                            return true;
                        }
                    }

                    // FALLBACK: clipboard + Ctrl+V into each edit
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
                            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                            logCallback?.Invoke("[AUTOMATOR] Auto-filled TightVNC passwords via Clipboard/Ctrl+V.");
                            return true;
                        }
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }

        // UPDATED: Try to auto-fill a license field using UIA or Clipboard paste fallback
        private static bool TryAutoFillLicense(Window win, Action<string>? logCallback)
        {
            try
            {
                var installerPath = InstallerService.CurrentFile;
                var siteKeyPath = FindSiteKeyNearInstaller(installerPath);
                if (siteKeyPath == null)
                {
                    // Also check temp sitekey published by InstallerService or by this automator
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
                                // FALLBACK: Focus the element and simulate pasting via Ctrl+V
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

        // Save a reduced UIA snapshot (ControlType, Name, AutomationId, BoundingRect) to disk for debugging and tuning
        private static void SaveUIASnapshot(Window window, string reason)
        {
            try
            {
                var root = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
                var outDir = Path.Combine(root, "uia_snapshots");
                Directory.CreateDirectory(outDir);

                var now = DateTime.UtcNow;
                var fileName = $"uia_{now:yyyyMMdd_HHmmss}_{reason}.json";
                var outPath = Path.Combine(outDir, fileName);

                var snapshot = new List<object>();
                var all = window.FindAllDescendants();
                foreach (var el in all)
                {
                    try
                    {
                        var rect = el.Properties.BoundingRectangle?.Value;
                        snapshot.Add(new
                        {
                            ControlType = el.ControlType.ToString(),
                            Name = el.Name,
                            AutomationId = el.Properties.AutomationId?.Value,
                            Bounding = rect == null ? null : new { rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height }
                        });
                    }
                    catch { }
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(outPath, JsonSerializer.Serialize(snapshot, options));
            }
            catch { }
        }

        // Special-case handling for Sophos installers which often use custom dialogs
        private static bool TryHandleSophos(Window win, Action<string>? log)
        {
            try
            {
                var title = win.AsWindow()?.Title ?? string.Empty;
                var curFile = InstallerService.CurrentFile ?? string.Empty;
                if (!(title.IndexOf("sophos", StringComparison.OrdinalIgnoreCase) >= 0 || curFile.IndexOf("sophos", StringComparison.OrdinalIgnoreCase) >= 0))
                    return false;

                log?.Invoke($"[AUTOMATOR-SOPHOS] Handling window '{title}'");

                // Toggle any agreement checkboxes first
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

                // Find actionable buttons (prefer explicit Continue/Install)
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
                                    btn.Invoke();
                                    log?.Invoke($"[AUTOMATOR-SOPHOS] Invoked button '{name}'");
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // Fallback: send Enter to the window
                try
                {
                    SendEnterToWindow(win);
                    log?.Invoke("[AUTOMATOR-SOPHOS] Sent Enter fallback");
                    return true;
                }
                catch { }
            }
            catch (Exception ex) { try { Logger.WriteException(ex, "TryHandleSophos"); } catch { } }

            return false;
        }

        // Try to detect WebView/CEF/Edge-hosted bootstrapper UI and attempt keyboard-based automation
        private static bool TryAutomateWebBootstrapper(Window window, Action<string>? log)
        {
            try
            {
                var all = window.FindAllDescendants();
                foreach (var el in all)
                {
                    try
                    {
                        var name = (el.Name ?? string.Empty).ToLowerInvariant();
                        var ctrl = el.ControlType;

                        // Heuristics: control name contains web-related token or control is a Pane (many WebView hosts expose a Pane)
                        if (name.Contains("web") || name.Contains("edge") || name.Contains("chromium") || name.Contains("browser") || name.Contains("cef") || (ctrl != null && ctrl.Equals(ControlType.Pane)))
                        {
                            log?.Invoke($"[AUTOMATOR-WEB] Possible web control detected: {el.ControlType}:{el.Name}");
                            try { SaveUIASnapshot(window, "web_detected"); } catch { }
                            try { window.AsWindow()?.Focus(); } catch { }
                            try { el.Focus(); } catch { }

                            // Strategy: SPACE (accept) then small delay then ENTER. Many web UIs map default action to Enter.
                            try
                            {
                                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
                                Thread.Sleep(150);
                                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                                log?.Invoke("[AUTOMATOR-WEB] Sent Space + Enter");
                            }
                            catch { }

                            // Try to find exposed UIA buttons inside the web control and invoke the most likely ones
                            bool invoked = false;
                            try
                            {
                                var innerButtons = el.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                                foreach (var ib in innerButtons)
                                {
                                    try
                                    {
                                        var iname = ib.Name ?? string.Empty;
                                        if (string.IsNullOrWhiteSpace(iname)) continue;
                                        if (System.Text.RegularExpressions.Regex.IsMatch(iname, "(next|install|accept|agree|continue|done|finish|close|ok)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                        {
                                            try { ib.AsButton()?.Invoke(); log?.Invoke($"[AUTOMATOR-WEB] Invoked inner button: {iname}"); invoked = true; break; } catch { }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }

                            if (invoked) return true;

                            // If no UIA buttons found, attempt keyboard navigation (Tab..Enter)
                            try
                            {
                                for (int i = 0; i < 8; i++)
                                {
                                    try { FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB); } catch { }
                                    Thread.Sleep(120);
                                }
                                try { FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN); log?.Invoke("[AUTOMATOR-WEB] Sent Tab x8 + Enter"); } catch { }
                            }
                            catch { }

                            // Final fallback: click the bottom-right quadrant of the window (common place for Next/Install buttons)
                            try
                            {
                                var rect = window.Properties.BoundingRectangle?.Value;
                                if (rect != null)
                                {
                                    double left = rect.Value.X;
                                    double top = rect.Value.Y;
                                    double width = rect.Value.Width;
                                    double height = rect.Value.Height;
                                    int cx = (int)Math.Round(left + width * 0.85);
                                    int cy = (int)Math.Round(top + height * 0.88);
                                    try
                                    {
                                        SetForegroundWindow(new IntPtr(Convert.ToInt32(window.Properties.NativeWindowHandle.Value)));
                                    }
                                    catch { }
                                    try
                                    {
                                        SetCursorPos(cx, cy);
                                        Thread.Sleep(80);
                                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                                        log?.Invoke($"[AUTOMATOR-WEB] Clicked bottom-right fallback at ({cx},{cy})");
                                        try { SaveUIASnapshot(window, "web_clicked_bottomright"); } catch { }
                                        return true;
                                    }
                                    catch (Exception ex) { try { Logger.WriteException(ex, "AutomatorWeb:click_fallback"); } catch { } }
                                }
                            }
                            catch { }

                            return false;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { try { Logger.WriteException(ex, "TryAutomateWebBootstrapper"); } catch { } }

            return false;
        }

        // NEW: Try to force-click a final button (Finish/Done/Close) while avoiding negative buttons
        private static bool TryForceFinalButtonClick(Window win, Action<string>? logCallback)
        {
            try
            {
                var buttons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                foreach (var b in buttons)
                {
                    try
                    {
                        var name = (b.Name ?? string.Empty).Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Avoid negative buttons
                        if (NegativeButtonWords.Any(nb => name.Contains(nb))) continue;

                        // If matches final-only list, invoke
                        if (FinalOnlyButtonNames.Any(f => name.Contains(f)))
                        {
                            var btn = b.AsButton();
                            if (btn != null && btn.IsEnabled)
                            {
                                btn.Invoke();
                                logCallback?.Invoke($"[AUTOMATOR] Invoked final button '{b.Name}'");
                                return true;
                            }
                        }

                        // Heuristic: texts that indicate completion
                        if (name.Contains("completed") || name.Contains("success") || name.Contains("installation complete") || name.Contains("installation finished"))
                        {
                            var btn = b.AsButton();
                            if (btn != null && btn.IsEnabled)
                            {
                                btn.Invoke();
                                logCallback?.Invoke($"[AUTOMATOR] Invoked likely final button '{b.Name}'");
                                return true;
                            }
                        }
                    }
                    catch { }
                }

                // If no UIA final button found, try Tab->Enter fallback to reach final button
                try
                {
                    win.Focus();
                    Thread.Sleep(120);
                    for (int i = 0; i < 10; i++)
                    {
                        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
                        Thread.Sleep(80);
                    }
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                    logCallback?.Invoke("[AUTOMATOR] Sent Tab x10 + Enter as final fallback.");
                    return true;
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "TryForceFinalButtonClick"); } catch { }
            }
            return false;
        }

        public static void InteractWithProcess(int pid, Action<string> logCallback, CancellationToken token, int timeoutMs = 120000, string? processNameHint = null)
        {
            // Fire-and-forget the async worker; capture the returned Task to avoid analyzer warnings
            _ = InteractWithProcessAsync(pid, logCallback, token, timeoutMs, processNameHint);
        }

        // P/Invoke for SetForegroundWindow and keybd_event as a fallback to send Enter
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

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
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            int? ppid = null;
                            try { ppid = mo["ParentProcessId"] != null ? Convert.ToInt32(mo["ParentProcessId"]) : (int?)null; } catch { }
                            map[pid] = ppid;
                        }
                        catch { }
                    }
                }

                // BFS from rootPid
                var q = new Queue<int>();
                q.Enqueue(rootPid);
                while (q.Count > 0)
                {
                    var cur = q.Dequeue();
                    results.Add(cur);
                    foreach (var kv in map)
                    {
                        if (kv.Value == cur)
                        {
                            q.Enqueue(kv.Key);
                        }
                    }
                }
            }
            catch (Exception ex) { try { Logger.WriteException(ex, "GetDescendantProcessIds"); } catch { } }
            return results;
        }

        // Core async worker that attaches to the process and automates windows
        private static async Task InteractWithProcessAsync(int pid, Action<string> logCallback, CancellationToken token, int timeoutMs = 120000, string? processNameHint = null)
        {
            try
            {
                logCallback?.Invoke($"[AUTOMATOR] InteractWithProcessAsync started for PID={pid}, hint={processNameHint}");

                // Publish sitekey to temp before any AHK or clipboard-based fallback runs.
                try
                {
                    // Prefer InstallerService.CurrentFile if available; otherwise use processNameHint to attempt resolution
                    string? installerPath = InstallerService.CurrentFile;
                    if (string.IsNullOrEmpty(installerPath) && !string.IsNullOrEmpty(processNameHint))
                    {
                        // Attempt to find a local copy in InstallerService.LocalCopies that matches processNameHint
                        try
                        {
                            var match = InstallerService.LocalCopies.FirstOrDefault(kv => Path.GetFileName(kv.Key).IndexOf(processNameHint ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!string.IsNullOrEmpty(match.Value))
                                installerPath = match.Value;
                        }
                        catch { }
                    }

                    var published = PublishSiteKeyToTemp(installerPath, logCallback);
                    if (published != null)
                    {
                        logCallback?.Invoke("[AUTOMATOR] Sitekey published to temp for use by AHK/clipboard fallbacks.");
                    }
                }
                catch (Exception ex)
                {
                    try { Logger.WriteException(ex, "InteractWithProcessAsync:PublishSiteKey"); } catch { }
                }

                // Create automation app and attach to windows for the target process and its descendants
                using (var automation = new UIA3Automation())
                {
                    var sw = Stopwatch.StartNew();
                    var seenWindowHandles = new HashSet<int>();

                    // We'll poll for windows belonging to the PID and its descendants until timeout or cancellation
                    while (!token.IsCancellationRequested && sw.ElapsedMilliseconds < timeoutMs)
                    {
                        try
                        {
                            // Get descendant PIDs to include child processes that host UI
                            var pids = GetDescendantProcessIds(pid);
                            foreach (var p in pids)
                            {
                                try
                                {
                                    var procs = Process.GetProcesses().Where(pp => pp.Id == p).ToArray();
                                    foreach (var proc in procs)
                                    {
                                        try
                                        {
                                            // Attach to top-level windows of this process
                                            var wins = automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(proc.Id).And(cf.ByControlType(ControlType.Window)));
                                            foreach (var w in wins)
                                            {
                                                try
                                                {
                                                    var win = w.AsWindow();
                                                    if (win == null) continue;

                                                    // Avoid reprocessing same window handle repeatedly
                                                    int h = 0;
                                                    try
                                                    {
                                                        var hv = win.Properties.NativeWindowHandle?.Value;
                                                        if (hv != null) h = Convert.ToInt32(hv);
                                                    }
                                                    catch { }

                                                    if (h != 0 && seenWindowHandles.Contains(h)) continue;
                                                    if (h != 0) seenWindowHandles.Add(h);

                                                    // Skip invisible/offscreen windows
                                                    if (IsWindowOffscreenSafe(win)) continue;

                                                    var title = win.Title ?? string.Empty;
                                                    logCallback?.Invoke($"[AUTOMATOR] Inspecting window: PID={proc.Id}, Title='{title}'");

                                                    // 1) Try TightVNC autofill
                                                    try
                                                    {
                                                        if (TryAutoFillTightVnc(win, logCallback))
                                                        {
                                                            logCallback?.Invoke("[AUTOMATOR] TightVNC autofill handled.");
                                                            continue;
                                                        }
                                                    }
                                                    catch (Exception ex) { try { Logger.WriteException(ex, "TryAutoFillTightVnc"); } catch { } }

                                                    // 2) Try license/sitekey autofill
                                                    try
                                                    {
                                                        if (TryAutoFillLicense(win, logCallback))
                                                        {
                                                            logCallback?.Invoke("[AUTOMATOR] License/sitekey autofill handled.");
                                                            continue;
                                                        }
                                                    }
                                                    catch (Exception ex) { try { Logger.WriteException(ex, "TryAutoFillLicense"); } catch { } }

                                                    // 3) Sophos special handling
                                                    try
                                                    {
                                                        if (TryHandleSophos(win, logCallback))
                                                        {
                                                            logCallback?.Invoke("[AUTOMATOR] Sophos window handled.");
                                                            continue;
                                                        }
                                                    }
                                                    catch (Exception ex) { try { Logger.WriteException(ex, "TryHandleSophos"); } catch { } }

                                                    // 4) Web bootstrapper heuristics
                                                    try
                                                    {
                                                        if (TryAutomateWebBootstrapper(win, logCallback))
                                                        {
                                                            logCallback?.Invoke("[AUTOMATOR] Web bootstrapper heuristics applied.");
                                                            continue;
                                                        }
                                                    }
                                                    catch (Exception ex) { try { Logger.WriteException(ex, "TryAutomateWebBootstrapper"); } catch { } }

                                                    // 5) Generic button heuristics (UIA)
                                                    try
                                                    {
                                                        var buttons = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                                                        foreach (var b in buttons)
                                                        {
                                                            try
                                                            {
                                                                var name = (b.Name ?? string.Empty).Trim();
                                                                if (string.IsNullOrWhiteSpace(name)) continue;
                                                                if (NegativeButtonWords.Any(nb => name.IndexOf(nb, StringComparison.OrdinalIgnoreCase) >= 0))
                                                                    continue;

                                                                if (System.Text.RegularExpressions.Regex.IsMatch(name, "(next|install|accept|agree|continue|done|finish|close|ok)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                                                {
                                                                    try
                                                                    {
                                                                        var btn = b.AsButton();
                                                                        if (btn != null && btn.IsEnabled)
                                                                        {
                                                                            btn.Invoke();
                                                                            logCallback?.Invoke($"[AUTOMATOR] Invoked button '{name}' via UIA.");
                                                                            break;
                                                                        }
                                                                    }
                                                                    catch { }
                                                                }
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                    catch { }

                                                    // 6) Try to force final button click if it looks like a final screen
                                                    try
                                                    {
                                                        if (AggressiveFinalClick)
                                                        {
                                                            if (TryForceFinalButtonClick(win, logCallback))
                                                            {
                                                                logCallback?.Invoke("[AUTOMATOR] Final button clicked (aggressive).");
                                                                continue;
                                                            }
                                                        }
                                                    }
                                                    catch { }

                                                    // 7) If nothing matched, attempt a safe Enter fallback
                                                    try
                                                    {
                                                        SendEnterToWindow(win);
                                                        logCallback?.Invoke("[AUTOMATOR] Sent Enter fallback to window.");
                                                    }
                                                    catch { }
                                                }
                                                catch (Exception ex) { try { Logger.WriteException(ex, "WindowLoop:inner"); } catch { } }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            try { Logger.WriteException(ex, "InteractWithProcessAsync:mainloop"); } catch { }
                        }

                        await Task.Delay(700, token).ConfigureAwait(false);
                    }

                    logCallback?.Invoke("[AUTOMATOR] InteractWithProcessAsync finished polling windows.");
                }
            }
            catch (OperationCanceledException)
            {
                logCallback?.Invoke("[AUTOMATOR] InteractWithProcessAsync cancelled.");
            }
            catch (Exception ex)
            {
                try { Logger.WriteException(ex, "InteractWithProcessAsync"); } catch { }
                logCallback?.Invoke("[AUTOMATOR] InteractWithProcessAsync encountered an error: " + ex.Message);
            }
        }
    }
}
