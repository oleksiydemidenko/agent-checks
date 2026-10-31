using System;
using System.IO;
using System.Linq;

namespace AgentChecks.Editor
{
    public static class DotnetPath
    {
        public static string Find()
        {
            var executable = Environment.OSVersion.Platform == PlatformID.Win32NT ? "dotnet.exe" : "dotnet";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT") }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                .Concat(new[]
                {
                    Path.Combine(home, ".dotnet"),
                    @"C:\Program Files\dotnet",
                    "/usr/share/dotnet",
                    "/usr/local/share/dotnet",
                    "/usr/lib/dotnet"
                })
                .Where(directory => !string.IsNullOrEmpty(directory))
                .Select(directory => Path.Combine(directory, executable));
            return candidates.FirstOrDefault(File.Exists) ?? executable;
        }
    }
}
