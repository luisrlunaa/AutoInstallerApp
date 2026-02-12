namespace AutoInstallerApp
{
    public static class AutoHotkeyHelper
    {
        public static string CreateAutoHotkeyScript()
        {
            string ahkPath = Path.Combine(Path.GetTempPath(), "auto_installer.ahk");

            string script = @"
#NoTrayIcon
SetTitleMatchMode, 2

Loop
{
    WinWaitActive, Setup
    ControlClick, Button1, Setup

    WinWaitActive, Installer
    ControlClick, Button1, Installer

    WinWaitActive, Installation
    ControlClick, Button1, Installation

    WinWaitActive, Next
    ControlClick, Button1, Next

    WinWaitActive, Siguiente
    ControlClick, Button1, Siguiente

    WinWaitActive, Finish
    ControlClick, Button1, Finish

    Sleep, 500
}
";

            File.WriteAllText(ahkPath, script);
            return ahkPath;
        }
    }
}
