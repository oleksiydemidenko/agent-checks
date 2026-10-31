using UnityEditor.PackageManager;

namespace AgentChecks.Editor
{
    public static class AgentChecksPackagePaths
    {
        private static string root;

        public static string Root => root ??= PackageInfo.FindForAssembly(typeof(AgentChecksPackagePaths).Assembly).resolvedPath;
    }
}
