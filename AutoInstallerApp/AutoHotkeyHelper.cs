namespace AutoInstallerApp
{
    using System.IO;

    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript(int pid, string exeName)
        {
            string script = $@"
            #NoTrayIcon
            SetTitleMatchMode, 2
            DetectHiddenWindows, On
            SetKeyDelay, 70, 70
            
            targetPID := {pid}
            exeName := ""{exeName}""
            isSophos := InStr(exeName, ""sophos"")
            
            Loop 
            {{
                ; 1. HANDLE SECURITY ALERTS & SMART SCREEN
                if WinExist(""ahk_class #32770"") or WinExist(""Security Warning"") or WinExist(""Windows protected"")
                {{
                    WinActivate
                    Sleep, 300
                    Send, {{Space}}
                    Sleep, 150
                    Send, {{Enter}}
                }}
            
                ; 2. SOPHOS SPECIFIC AUTOMATION (Title based, as PID changes)
                if WinExist(""Sophos Setup"") or WinExist(""Sophos Endpoint"")
                {{
                    WinActivate
                    Sleep, 500
                    Send, {{Enter}}
                    Sleep, 500
                    Send, {{Space}}
                    
                    ; Fallback: Click the 'Install' button area (bottom right)
                    WinGetPos, , , w, h, A
                    if (w > 0) {{
                        targetX := w - 120
                        targetY := h - 70
                        Click, %targetX% %targetY%
                    }}
                    Sleep, 4000 ; Long sleep to wait for the next screen/processing
                }}
            
                ; 3. TIGHTVNC SPECIFIC AUTOMATION (Forced Focus)
                if WinExist(""TightVNC"") 
                {{
                    WinActivate
                    WinWaitActive, TightVNC, , 2
                    if (ErrorLevel = 0) 
                    {{
                        Loop, 4
                        {{
                            SendRaw, aica
                            Sleep, 200
                            Send, {{Tab}}
                            Sleep, 200
                        }}
                        Send, {{Enter}}
                        Sleep, 2000
                    }}
                }}
            
                ; 4. UNIVERSAL FALLBACK (For the original C# triggered PID)
                if WinExist(""ahk_pid "" . targetPID)
                {{
                    WinActivate
                    Send, {{Space}}
                    Sleep, 200
                    Send, {{Enter}}
                    
                    WinGetPos, , , w, h, ahk_pid %targetPID%
                    if (w > 0) {{
                        targetX := w - 100
                        targetY := h - 60
                        Click, %targetX% %targetY%
                    }}
                }}
            
                ; 5. EXIT CONDITION
                ; If it's Sophos, stay alive as long as the Sophos window exists.
                ; Otherwise, exit when the original PID no longer has active windows.
                if (isSophos)
                {{
                    if !WinExist(""Sophos Setup"") and !WinExist(""Sophos Endpoint"")
                        ExitApp
                }}
                else
                {{
                    if !WinExist(""ahk_pid "" . targetPID) and !WinExist(""TightVNC"")
                        ExitApp
                }}
            
                Sleep, 1000
            }}";

            string ahkPath = Path.Combine(Path.GetTempPath(), $"auto_installer_{pid}.ahk");
            File.WriteAllText(ahkPath, script);
            return ahkPath;
        }
    }
}