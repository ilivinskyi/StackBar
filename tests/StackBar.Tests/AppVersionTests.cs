using System;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class AppVersionTests
    {
        [Fact]
        public void Display_version_drops_the_revision_number()
        {
            Assert.Equal("1.2.3", AppInfo.FormatVersion(new Version(1, 2, 3, 0)));
        }

        [Fact]
        public void Display_version_fills_a_missing_build_number_with_zero()
        {
            Assert.Equal("2.0.0", AppInfo.FormatVersion(new Version(2, 0)));
        }

        [Fact]
        public void Display_version_comes_from_the_app_assembly_not_a_hardcoded_string()
        {
            var assemblyVersion = typeof(AppInfo).Assembly.GetName().Version;

            Assert.Equal($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}", AppInfo.DisplayVersion);
        }
    }
}
