using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AutoInstallerApp.AutoInstallerAgent
{
    internal static class AgentMain
    {
        private const string PipeName = "AutoInstallerAgentPipe";

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static void RunAgentLoop()
        {
            try
            {
                Logger.Write($"[AGENT] Agent starting. PID={Process.GetCurrentProcess().Id}");

                // Simple single-connection server loop
                while (true)
                {
                    // Create a pipe security that allows connections from Everyone (best-effort)
                    var ps = new PipeSecurity();
                    try
                    {
                        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                        ps.AddAccessRule(new PipeAccessRule(everyone, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    }
                    catch { }

                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
                    {
                        try { server.SetAccessControl(ps); } catch { }
                        try
                        {
                            Logger.Write("[AGENT] Waiting for connection...");
                            server.WaitForConnection();
                        }
                        catch (Exception ex)
                        {
                            Logger.Write("[AGENT] WaitForConnection failed: " + ex.Message);
                            continue;
                        }

                        Logger.Write("[AGENT] Client connected to pipe.");

                        using (var sr = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true))
                        using (var sw = new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true })
                        {
                            string? request = null;
                            try
                            {
                                request = sr.ReadLine();
                            }
                            catch (Exception ex)
                            {
                                Logger.Write("[AGENT] ReadLine failed: " + ex.Message);
                            }

                            if (string.IsNullOrWhiteSpace(request))
                            {
                                try { sw.WriteLine("{\"status\":\"empty\"}"); } catch { }
                                continue;
                            }

                            try
                            {
                                var doc = JsonDocument.Parse(request);
                                if (doc.RootElement.TryGetProperty("cmd", out var cmdEl))
                                {
                                    var cmd = cmdEl.GetString() ?? string.Empty;
                                    if (string.Equals(cmd, "attach", StringComparison.OrdinalIgnoreCase))
                                    {
                                        int pid = doc.RootElement.GetProperty("pid").GetInt32();
                                        // Perform automation by calling existing automator
                                        _ = Task.Run(() =>
                                        {
                                            try
                                            {
                                                Logger.Write($"[AGENT] Attaching to PID {pid}");

                                                // Diagnostic: agent elevation state
                                                bool agentIsElevated = false;
                                                try
                                                {
                                                    var id = WindowsIdentity.GetCurrent();
                                                    if (id != null)
                                                    {
                                                        using (id)
                                                        {
                                                            var principal = new WindowsPrincipal(id);
                                                            agentIsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
                                                        }
                                                    }
                                                }
                                                catch { }

                                                Logger.Write($"[AGENT] Agent elevated: {agentIsElevated}");

                                                // Diagnostic: target process info
                                                try
                                                {
                                                    var p = Process.GetProcessById(pid);
                                                    if (p != null)
                                                    {
                                                        Logger.Write($"[AGENT] Target process: Id={p.Id}, Name={p.ProcessName}, HasExited={p.HasExited}, SessionId={p.SessionId}");
                                                    }

                                                    // Try to get owner via WMI
                                                    try
                                                    {
                                                        string owner = "(unknown)";
                                                        try
                                                        {
                                                            using (var mos = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ProcessId = {pid}"))
                                                            {
                                                                foreach (ManagementObject mo in mos.Get())
                                                                {
                                                                    var outParams = mo.InvokeMethod("GetOwner", null, null);
                                                                    if (outParams is ManagementBaseObject mbo)
                                                                    {
                                                                        owner = (mbo["Domain"] as string)?.Trim() + "\\" + (mbo["User"] as string)?.Trim();
                                                                    }
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        catch { }
                                                        Logger.Write($"[AGENT] Target owner: {owner}");
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Logger.Write($"[AGENT] Could not query target process info: {ex.Message}");
                                                    }

                                                    // Diagnostic: try OpenProcess to detect Access denied
                                                    try
                                                    {
                                                        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                                                        if (h == IntPtr.Zero)
                                                        {
                                                            int err = Marshal.GetLastWin32Error();
                                                            Logger.Write($"[AGENT] OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) failed, GetLastError={err}");
                                                        }
                                                        else
                                                        {
                                                            Logger.Write($"[AGENT] OpenProcess succeeded (got handle) for PID {pid}");
                                                            CloseHandle(h);
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Logger.Write($"[AGENT] OpenProcess check exception: {ex.Message}");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.Write($"[AGENT] Error enumerating target process: {ex.Message}");
                                                }

                                                // Use InstallerUiAutomator to attach and interact
                                                string? pname = null;
                                                try { pname = Process.GetProcessById(pid).ProcessName; } catch { }
                                                // use non-null log callback
                                                Action<string> logcb = m => { try { Logger.Write("[AGENT LOG] " + m); } catch { } };
                                                InstallerUiAutomator.InteractWithProcess(pid, logcb, CancellationToken.None, timeoutMs: 180000, processNameHint: pname);
                                            }
                                            catch (Exception ex)
                                            {
                                                Logger.Write("[AGENT ERROR] " + ex.ToString());
                                            }
                                        });

                                        sw.WriteLine("{\"status\":\"ok\"}");
                                    }
                                    else if (cmd == "ping")
                                    {
                                        sw.WriteLine("{\"status\":\"pong\"}");
                                    }
                                    else
                                    {
                                        sw.WriteLine("{\"status\":\"unknown_cmd\"}");
                                    }
                                }
                                else
                                {
                                    sw.WriteLine("{\"status\":\"bad_request\"}");
                                }
                            }
                            catch (Exception ex)
                            {
                                try { sw.WriteLine(JsonSerializer.Serialize(new { status = "error", message = ex.Message })); } catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.Write("[AGENT LOOP ERROR] " + ex.ToString()); } catch { }
            }
        }
    }
}
