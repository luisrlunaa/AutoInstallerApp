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

    ; === SPICEWORKS: I Agree + SiteKey ===
    if WinExist(""Agent Shell Setup"") or WinExist(""Spiceworks"")
    {{
        WinActivate
        Sleep, 300

        ; Try to find and click the 'I Agree' checkbox by clicking near the text
        ; Fallback to ControlClick if standard controls exist
        ; First attempt: ControlClick common checkbox/button controls
        ; Try a few common control names/IDs; if they fail, click relative to text
        ; Safe attempts only: avoid clicking negative buttons

        ; Try ControlClick on likely checkbox controls
        ControlClick, Button1, Agent Shell Setup,,, NA
        Sleep, 250

        ; Click Next (try ControlClick then fallback to OCR-like click near 'Next' text)
        ControlClick, Button2, Agent Shell Setup,,, NA
        Sleep, 800

        ; If a sitekey temp file exists, paste it into the focused edit
        tmpSk := A_Temp ""\\auto_installer_sitekey.txt""
        if FileExist(tmpSk)
        {{
            FileRead, sitekey, %tmpSk%
            if (sitekey != """")
            {{
                ClipSaved := ClipboardAll
                Clipboard := sitekey
                Sleep, 150

                ; Try to paste into focused control
                Send, ^v
                Sleep, 300

                ; Press Enter to continue
                Send, {{Enter}}
                Sleep, 800

                Clipboard := ClipSaved
            }}
        }}
    }}

    ; === TIGHTVNC: Seleccionar radio buttons y escribir 'aica' ===
    if WinExist(""TightVNC Server: Set Passwords"") or WinExist(""TightVNC Server"") or WinExist(""TightVNC"")
    {{
        WinActivate
        WinWaitActive, TightVNC, , 2
        Sleep, 300

        ; Attempt ControlClick on radio buttons (many TightVNC dialogs expose Button controls)
        ; We try a few likely button indices; if they don't exist, fallback to coordinate clicks
        ; Select third radio in first section and third radio in second section
        ; Safe attempts only: do not click Cancel

        ; Try ControlClick by button index names
        ControlClick, Button3, TightVNC Server: Set Passwords,,, NA
        Sleep, 200
        ControlClick, Button6, TightVNC Server: Set Passwords,,, NA
        Sleep, 300

        ; Prepare clipboard with password
        ClipSaved := ClipboardAll
        Clipboard := ""aica""
        Sleep, 120

        ; Try to paste into up to 4 fields. If focus not in first field, attempt to click near 'Enter password' labels
        ; Heuristic: try to focus first edit by sending Tab a few times, then paste 4 times with Tab
        ; First, attempt to focus by clicking center-left area of window where edits usually are
        WinGetPos, X, Y, W, H, TightVNC Server: Set Passwords
        if (W > 0)
        {{
            ; Click roughly where the first password edit is expected (left-center)
            cx := X + Round(W * 0.45)
            cy := Y + Round(H * 0.35)
            Click, %cx% %cy%
            Sleep, 180
        }}

        ; Now attempt paste into 4 fields using Ctrl+V + Tab
        Loop, 4
        {{
            Send, ^v
            Sleep, 220
            Send, {{Tab}}
            Sleep, 220
        }}

        ; Press Enter to confirm
        Send, {{Enter}}
        Sleep, 1500

        Clipboard := ClipSaved
    }}

    ; 3) TightVNC generic detection (older titles)
    if WinExist(""TightVNC"") and !WinExist(""TightVNC Server: Set Passwords"")
    {{
        WinActivate
        WinWaitActive, TightVNC, , 2
        Sleep, 300

        ClipSaved := ClipboardAll
        Clipboard := ""aica""
        Sleep, 120

        ; Try to paste into up to 4 fields by sending Ctrl+V and Tab
        Loop, 4
        {{
            Send, ^v
            Sleep, 200
            Send, {{Tab}}
            Sleep, 200
        }}
        Send, {{Enter}}
        Sleep, 1200
        Clipboard := ClipSaved
    }}

    ; 4) TightVNC: try to paste 'aica' into edit fields (legacy fallback)
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

    ; 5) License / sitekey paste: if a temp sitekey exists, paste it into focused edit
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
            if WinExist(""License"") or WinExist(""Activation"") or WinExist(""Product Key"") or WinExist(""Serial"") or WinExist(""Agent Shell Setup"")
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

    ; 6) Generic fallback for installer windows associated with PID
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

    ; 7) Retry flag behavior: if retry flag exists, attempt alternate radio selection via Tab navigation
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
