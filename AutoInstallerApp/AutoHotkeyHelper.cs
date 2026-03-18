namespace AutoInstallerApp
{
    using System.Diagnostics;
    using System.IO;

    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript(int pid, string exeName)
        {
            // The script focuses on safe automation:
            // - Avoid clicking negative buttons
            // - Paste sitekey from temp if present
            // - Paste TightVNC password "aica" into edit fields
            // - Respect a retry flag file to change radio selection behavior if needed
            string script = $@"
                #NoTrayIcon
                SetTitleMatchMode, 2
                DetectHiddenWindows, On
                SetKeyDelay, 70, 70
                SetControlDelay, 70
                SetWinDelay, 70
                
                targetPID := {pid}
                exeName := ""{exeName}""
                isSophos := InStr(exeName, ""sophos"")
                retryFlag := A_Temp ""\\auto_installer_retry_{Process.GetCurrentProcess().Id}.flag""
                
                ; Helper: safe paste from clipboard
                SafePaste()
                {{
                    Send, ^v
                    Sleep, 120
                }}
                
                Loop
                {{
                    ; If the target PID no longer exists and no known windows remain, exit
                    Process, Exist, %targetPID%
                    if (ErrorLevel = 0)
                    {{
                        ; If Sophos, keep alive until its windows disappear
                        if (isSophos)
                        {{
                            if !WinExist(""Sophos Setup"") and !WinExist(""Sophos Endpoint"")
                                ExitApp
                        }}
                        else
                        {{
                            ExitApp
                        }}
                    }}
                
                    ; 1) Handle common security dialogs (SmartScreen, Security Warning)
                    if WinExist(""ahk_class #32770"") or WinExist(""Security Warning"") or WinExist(""Windows protected"")
                    {{
                        WinActivate
                        Sleep, 300
                        ; Prefer pressing Space then Enter (accept)
                        Send, {{Space}}
                        Sleep, 150
                        Send, {{Enter}}
                        Sleep, 400
                    }}
                
                    ; 2) Sophos specific flows (avoid clicking Cancel)
                    if WinExist(""Sophos Setup"") or WinExist(""Sophos Endpoint"")
                    {{
                        WinActivate
                        Sleep, 500
                        Send, {{Enter}}
                        Sleep, 500
                        Send, {{Space}}
                        Sleep, 500
                        ; Click approximate Install area if needed (bottom-right) but avoid Cancel
                        WinGetPos, X, Y, W, H, A
                        if (W > 0)
                        {{
                            targetX := X + W - 120
                            targetY := Y + H - 70
                            Click, %targetX% %targetY%
                        }}
                        Sleep, 4000
                    }}
                
                    ; 3) TightVNC: try to paste 'aica' into edit fields
                    if WinExist(""TightVNC"") or WinExist(""TightVNC Server"")
                    {{
                        WinActivate
                        WinWaitActive, TightVNC, , 2
                        if (ErrorLevel = 0)
                        {{
                            ; Put password into clipboard and paste into fields
                            ClipSaved := ClipboardAll
                            Clipboard := ""aica""
                            Sleep, 120
                            ; Try to paste into up to 4 fields
                            Loop, 4
                            {{
                                Send, ^v
                                Sleep, 200
                                Send, {{Tab}}
                                Sleep, 200
                            }}
                            Send, {{Enter}}
                            Sleep, 2000
                            Clipboard := ClipSaved
                        }}
                    }}
                
                    ; 4) License / sitekey paste: if a temp sitekey exists, paste it into focused edit
                    tmpSk := A_Temp ""\\auto_installer_sitekey.txt""
                    if FileExist(tmpSk)
                    {{
                        ; Read sitekey
                        FileRead, sitekey, %tmpSk%
                        if (sitekey != """")
                        {{
                            ; Put into clipboard and attempt paste into focused control
                            ClipSaved := ClipboardAll
                            Clipboard := sitekey
                            Sleep, 120
                            ; Try to find likely license windows and paste
                            if WinExist(""License"") or WinExist(""Activation"") or WinExist(""Product Key"") or WinExist(""Serial"")
                            {{
                                WinActivate
                                Sleep, 200
                                Send, ^v
                                Sleep, 200
                                Send, {{Enter}}
                                Sleep, 500
                            }}
                            Clipboard := ClipSaved
                        }}
                    }}
                
                    ; 5) Generic fallback for installer windows associated with PID
                    WinGet, idList, List, ahk_pid %targetPID%
                    Loop, %idList%
                    {{
                        this_id := idList%A_Index%
                        WinActivate, ahk_id %this_id%
                        Sleep, 200
                        ; Try to press Space then Enter to accept default action
                        Send, {{Space}}
                        Sleep, 150
                        Send, {{Enter}}
                        Sleep, 300
                    }}
                
                    ; 6) Retry flag behavior: if retry flag exists, attempt alternate radio selection via Tab navigation
                    if FileExist(retryFlag)
                    {{
                        ; Try to tab to radio buttons and toggle
                        WinGet, idList2, List, ahk_pid %targetPID%
                        Loop, %idList2%
                        {{
                            this_id := idList2%A_Index%
                            WinActivate, ahk_id %this_id%
                            Sleep, 200
                            ; Tab through controls to find radio buttons; attempt to toggle second option
                            Loop, 20
                            {{
                                Send, {{Tab}}
                                Sleep, 120
                            }}
                            ; Press Space to select
                            Send, {{Space}}
                            Sleep, 200
                        }}
                    }}
                
                    Sleep, 1000
                }}
                
                ; End of script
                ";

            string ahkPath = Path.Combine(Path.GetTempPath(), $"auto_installer_{pid}.ahk");
            File.WriteAllText(ahkPath, script);
            return ahkPath;
        }
    }
}
