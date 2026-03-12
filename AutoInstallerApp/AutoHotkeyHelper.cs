namespace AutoInstallerApp
{
    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript(int pid)
        {
            string ahkPath = Path.Combine(Path.GetTempPath(), "auto_installer_dynamic.ahk");

            string template = @"#NoTrayIcon
SetTitleMatchMode, 2
DetectHiddenWindows, On

targetPID := __PID__
siteKeyPath := ""__SITEKEY__""

Loop
{
    ; 1. SECURITY ALERTS (SmartScreen / Open File Warning)
    if WinExist(""ahk_class #32770"") or WinExist(""Windows protected your PC"") or WinExist(""Security Warning"")
    {
        WinActivate
        Sleep, 200
        ControlClick, More info, A,,,, NA
        Sleep, 200
        Send, {Alt Down}r{Alt Up}
        Send, {Enter}
    }

    ; 1.b TIGHTVNC PASSWORD DIALOG - rellenar Edit1..Edit4 y pulsar OK
    if WinExist(""TightVNC Server Setup"") or WinExist(""TightVNC"")
    {
        WinActivate
        Sleep, 200
        ; Edit1/Edit2 -> main password, Edit3/Edit4 -> view-only password (titles may vary by installer version)
        ControlSetText, Edit1, aica, A
        ControlSetText, Edit2, aica, A
        ControlSetText, Edit3, aica, A
        ControlSetText, Edit4, aica, A
        Sleep, 150
        ControlClick, Button1, A
    }

    ; 2. Installer windows for target PID
    WinGet, id, List,,, Program Manager
    Loop, %id%
    {
        this_id := id%A_Index%
        WinGet, thisPID, PID, ahk_id %this_id%

        if (thisPID = targetPID)
        {
            WinActivate, ahk_id %this_id%
            Sleep, 300

            buttons := [""Next"", ""Siguiente"", ""Install"", ""Instalar"", ""Finish"", ""Aceptar"", ""OK"", ""Continuar"", ""Yes"", ""Close""]

            for index, label in buttons
            {
                ControlClick, %label%, ahk_id %this_id%
                Sleep, 200
            }

            ControlGetPos, x, y, w, h, , ahk_id %this_id%
            if (w > 0 and h > 0)
            {
                Click, % (x + w - 80) % , % (y + h - 40)
            }
        }
    }

    Sleep, 500
}
";

            var script = template.Replace("__PID__", pid.ToString());
            File.WriteAllText(ahkPath, script);
            return ahkPath;
        }
    }
}
