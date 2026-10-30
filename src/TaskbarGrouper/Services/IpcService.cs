using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace TaskbarGrouper.Services
{
    public sealed class IpcService : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr hPipe, out uint serverProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly Action<string, int, int> _onOpenGroupRequested;
        private readonly Action _onManagerRequested;
        private readonly Action _onExitRequested;
        private readonly Thread _listenerThread;
        private volatile bool _isDisposed;
        private static string? _pipeName;

        public static string PipeName
        {
            get
            {
                if (_pipeName == null)
                {
                    string userSid = WindowsIdentity.GetCurrent().User?.Value ?? "DefaultUser";
                    int sessionId = Process.GetCurrentProcess().SessionId;
                    string modeTag = AppInfo.IsPortableMode ? "Port" : "Inst";
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                    {
                        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(userSid));
                        string hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
                        _pipeName = $"StackBar_{modeTag}_{sessionId}_{hex}";
                    }
                }
                return _pipeName;
            }
        }

        public IpcService(Action<string, int, int> onOpenGroupRequested, Action onManagerRequested, Action onExitRequested)
        {
            _onOpenGroupRequested = onOpenGroupRequested;
            _onManagerRequested = onManagerRequested;
            _onExitRequested = onExitRequested;

            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "StackBar_IPC_Listener"
            };
            _listenerThread.Start();
        }

        public static bool SendCommand(string command)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(100);

                    // Grant foreground activation permission to the server instance
                    if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint serverPid))
                    {
                        AllowSetForegroundWindow(serverPid);
                    }

                    byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                    client.Write(data, 0, data.Length);
                    client.Flush();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void ListenLoop()
        {
            var user = WindowsIdentity.GetCurrent().User;
            var sec = new PipeSecurity();
            if (user != null)
            {
                sec.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }

            while (!_isDisposed)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        1024,
                        1024,
                        sec);

                    server.WaitForConnection();

                    using (var reader = new StreamReader(server, Encoding.UTF8))
                    {
                        string line = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            ProcessPayload(line.Trim());
                        }
                    }
                }
                catch
                {
                    if (_isDisposed) break;
                    Thread.Sleep(50);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private void ProcessPayload(string line)
        {
            var parts = line.Split('|');
            string cmd = parts[0];

            if (cmd.Equals("OPEN", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
            {
                string groupId = parts[1];
                int.TryParse(parts[2], out int x);
                int.TryParse(parts[3], out int y);
                _onOpenGroupRequested(groupId, x, y);
            }
            else if (cmd.Equals("MANAGER", StringComparison.OrdinalIgnoreCase))
            {
                _onManagerRequested();
            }
            else if (cmd.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                _onExitRequested();
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            try
            {
                // Unblock server by connecting dummy client
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(20);
                }
            }
            catch { }
        }
    }
}
