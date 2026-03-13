namespace AutoInstallerApp
{
    using System.IO;

    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript(int pid)
        {
            // Eliminamos la búsqueda de nombres de botones y usamos fuerza bruta de teclado
            string script = $@"
#NoTrayIcon
SetTitleMatchMode, 2
DetectHiddenWindows, On
SetKeyDelay, 50, 50

targetPID := {pid}

Loop
{{
    ; 1. ALERTAS DE SEGURIDAD (SmartScreen / Botón RUN)
    if WinExist(""ahk_class #32770"") or WinExist(""Security Warning"") or WinExist(""Windows protected"")
    {{
        WinActivate
        Sleep, 200
        Send, {{Space}} ; Selecciona el botón por defecto
        Sleep, 100
        Send, {{Enter}}
    }}

    ; 2. TIGHTVNC (Relleno de 4 campos con TAB)
    if WinExist(""TightVNC"")
    {{
        WinActivate
        Loop, 4
        {{
            SendRaw, aica
            Sleep, 100
            Send, {{Tab}}
        }}
        Sleep, 100
        Send, {{Enter}}
    }}

    ; 3. SOPHOS Y OTROS (Fuerza bruta para Web-UI)
    WinGet, id, List,,, Program Manager
    Loop, %id%
    {{
        this_id := id%A_Index%
        WinGet, thisPID, PID, ahk_id %this_id%
        if (thisPID = targetPID)
        {{
            WinActivate, ahk_id %this_id%
            
            ; Sophos: Enter y Espacio son universales para botones web enfocados
            Send, {{Space}}
            Sleep, 200
            Send, {{Enter}}
            
            ; Clic de respaldo en esquina inferior derecha (donde suelen estar los botones)
            WinGetPos, , , w, h, ahk_id %this_id%
            if (w > 0) {{
                Click, % (w - 100), % (h - 60)
            }}
        }}
    }}

; Si el proceso ya no tiene ventanas, salir del script
    if !WinExist(""ahk_pid "" . targetPID)
        ExitApp

    Sleep, 1000
}}
";
            string ahkPath = Path.Combine(Path.GetTempPath(), "auto_installer_dynamic.ahk");
            File.WriteAllText(ahkPath, script);
            return ahkPath;
        }
    }
}