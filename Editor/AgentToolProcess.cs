using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentChecks.Editor
{
    public sealed class AgentToolProcess
    {
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _error;

        public AgentToolProcess(Process process)
        {
            _process = process;
            _error = process.StandardError.ReadToEndAsync();
            _output = process.StandardOutput.ReadToEndAsync();
        }

        public bool HasExited => _process.HasExited && _output.IsCompleted && _error.IsCompleted;
        public int ExitCode => _process.ExitCode;
        public string Output => _output.Result;
        public string Error => _error.Result;

        public static AgentToolProcess StartDotnetProject(string root, string project, params string[] arguments)
        {
            var commandLine = new[] { "run", "--project", System.IO.Path.Combine(root, project), "--" }.Concat(arguments);
            var start = new ProcessStartInfo(DotnetPath.Find(), string.Join(" ", commandLine.Select(argument => "\"" + argument.Replace("\"", "\\\"") + "\"")))
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            return new AgentToolProcess(Process.Start(start));
        }
    }
}
