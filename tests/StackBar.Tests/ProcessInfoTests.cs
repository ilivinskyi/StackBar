using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class ProcessInfoTests
    {
        [Fact]
        public void User_sid_matches_WindowsIdentity()
        {
            Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, ProcessInfo.UserSid);
        }

        [Fact]
        public void Session_id_matches_Process()
        {
            Assert.Equal(Process.GetCurrentProcess().SessionId, ProcessInfo.SessionId);
        }

        [Fact]
        public void Start_time_matches_Process()
        {
            long expected = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            Assert.InRange(ProcessInfo.StartTimeUtcTicks, expected - TimeSpan.TicksPerMillisecond, expected + TimeSpan.TicksPerMillisecond);
        }

        [Fact]
        public void Pipe_name_is_unchanged_from_the_previous_formula()
        {
            // Launcher and resident must agree on the name; this is how earlier versions built it
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            int session = Process.GetCurrentProcess().SessionId;
            string hex;
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                hex = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sid))).Replace("-", "").Substring(0, 8);
            }
            string mode = AppInfo.IsPortableMode ? "Port" : "Inst";

            Assert.Equal($"StackBar_{mode}_{session}_{hex}", IpcService.PipeName);
        }
    }
}
