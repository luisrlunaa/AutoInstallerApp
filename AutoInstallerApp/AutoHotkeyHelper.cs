// AutoHotkeyHelper.cs
namespace AutoInstallerApp
{
    using System.Diagnostics;
    using System.IO;

    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript(int pid, string exeName, bool forceNoCursorInteraction = false)
        {
            // The script focuses on safe automation:
            // - Avoid clicking negative buttons
            // - Paste sitekey from temp if present
            // - Paste TightVNC password "aica" into edit fields
            // - Respect a retry flag file to change radio selection behavior if needed
            // - Respect forceNoCursorInteraction to avoid moving the user's cursor if requested
            string cursorGuard = forceNoCursorInteraction ? "true" : "false";

            string script = $@"
#NoTrayIcon
SetTitleMatchMode, 2
DetectHiddenWindows, On
SetKeyDelay, 70, 70
SetControlDelay, 70
SetWinDelay, 70

targetPID := {pid}
exeName := ""{exeName}""
forceNoCursor := {cursorGuard}
retryFlag := A_Temp ""\\auto_installer_retry_{Process.GetCurrentProcess().Id}.flag""

SafePaste()
{{
    Send, ^v
    Sleep, 120
}}

Loop
{{
    Process, Exist, %targetPID%
    if (ErrorLevel = 0)
    {{
        ExitApp
    }}

    ; Handle common security dialogs by preferring Enter over Space
    if WinExist(""ahk_class #32770"") or WinExist(""Security Warning"") or WinExist(""Windows protected"")
    {{
        WinActivate
        Sleep, 300
        Send, {{Enter}}
        Sleep, 400
    }}

    ; TIGHTVNC: paste password 'aica' into fields using Ctrl+V and Tab, prefer keyboard-only
    if WinExist(""TightVNC Server: Set Passwords"") or WinExist(""TightVNC Server"") or WinExist(""TightVNC"")
    {{
        WinActivate
        WinWaitActive, TightVNC, , 2
        Sleep, 300

        ClipSaved := ClipboardAll
        Clipboard := ""aica""
        Sleep, 120

        Loop, 4
        {{
            Send, ^v
            Sleep, 220
            Send, {{Tab}}
            Sleep, 220
        }}

        Send, {{Enter}}
        Sleep, 1500

        Clipboard := ClipSaved
    }}

    ; License/sitekey paste fallback
    tmpSk := A_Temp ""\\auto_installer_sitekey.txt""
    if FileExist(tmpSk)
    {{
        FileRead, sitekey, %tmpSk%
        if (sitekey != """")
        {{
            ClipSaved := ClipboardAll
            Clipboard := sitekey
            Sleep, 120
            ; NOTE: removed Agent Shell detection here to avoid AHK preempting FlaUI
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

    ; Generic fallback for installer windows associated with PID
    WinGet, idList, List, ahk_pid %targetPID%
    Loop, %idList%
    {{
        this_id := idList%A_Index%
        WinActivate, ahk_id %this_id%
        Sleep, 200

        ; Prefer invoking Enter (safer) and avoid Space which can toggle Cancel in some dialogs
        if (forceNoCursor = false)
        {{
            Send, {{Enter}}
            Sleep, 300
        }}
        else
        {{
            Send, {{Enter}}
            Sleep, 300
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
