using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Facts about the current process read straight from Windows. Every pin click starts a short-lived
    /// StackBar process that only forwards the click, so it must not pay for the .NET helpers
    /// (WindowsIdentity, Process) that load and initialise far more than these few values need.
    /// </summary>
    internal static class ProcessInfo
    {
        private static string? _userSid;

        /// <summary>The user's SID ("S-1-5-21-..."), the same string WindowsIdentity.GetCurrent().User gives.</summary>
        public static string UserSid => _userSid ??= ReadUserSid() ?? WindowsIdentity.GetCurrent().User?.Value ?? "DefaultUser";

        public static int SessionId =>
            ProcessIdToSessionId(GetCurrentProcessId(), out uint session) ? (int)session : System.Diagnostics.Process.GetCurrentProcess().SessionId;

        /// <summary>When this process started, in UTC ticks (what Process.StartTime gives, without its cost).</summary>
        public static long StartTimeUtcTicks =>
            GetProcessTimes(GetCurrentProcess(), out long creation, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(creation).Ticks
                : System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

        private static string? ReadUserSid()
        {
            const uint TOKEN_QUERY = 0x0008;
            const int TokenUser = 1;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return null;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int length);
                if (length <= 0) return null;
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return null;

                // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (!ConvertSidToStringSid(sid, out IntPtr text)) return null;
                try
                {
                    return Marshal.PtrToStringUni(text);
                }
                finally
                {
                    LocalFree(text);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                CloseHandle(token);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
