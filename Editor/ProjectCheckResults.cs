using System.Collections.Generic;

namespace AgentChecks.Editor
{
    public sealed class ProjectCheckResults
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Notes { get; } = new();
        public List<string> IgnoredConsolePrefixes { get; } = new();
    }
}
