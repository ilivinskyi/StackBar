using System.IO;
using TaskbarGrouper.Services;

namespace System.Runtime.CompilerServices
{
    // Lets the C# compiler emit a module initializer on .NET Framework (the attribute ships only with .NET 5+)
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace StackBar.Tests
{
    internal static class TestLogSetup
    {
        /// <summary>
        /// Runs before any test in this assembly: AppLogger is static, so without this a test class that
        /// forgets to redirect it would write into the developer's real %AppData%\StackBar\logs.
        /// </summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void RedirectLog()
        {
            AppLogger.Initialize(Path.Combine(Path.GetTempPath(), "StackBarTests_logs"));
        }
    }
}
