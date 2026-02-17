
namespace AutoInstallerApp
{
    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript(int pid)
        {
            string ahkPath = Path.Combine(Path.GetTempPath(), "auto_installer_dynamic.ahk");

            string script = @"
#NoTrayIcon
SetTitleMatchMode, 2
DetectHiddenWindows, On

targetPID := {pid}

Loop
{WinGet,id, List,,, Program Manager
    Loop, %id%
    {
        this_id := id%A_Index%
        WinGet, thisPID, PID, ahk_id %this_id%

        if (thisPID = targetPID)
        {
            WinActivate, ahk_id %this_id%
            Sleep, 300

            ; Buscar botones por texto común
            buttons := [""Next"", ""Siguiente"", ""Install"", ""Instalar"", ""Finish"", ""Aceptar"", ""OK"", ""Continuar"", ""Yes"", ""Close""]

            for index, label in buttons
            {
                ControlClick, %label%, ahk_id %this_id%
                Sleep, 200
            }

            ; Click por coordenadas si no hay controles
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

            File.WriteAllText(ahkPath, script);
            return ahkPath;
        }
    }
}
