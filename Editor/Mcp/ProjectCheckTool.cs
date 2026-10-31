using AgentChecks.Editor;
using System;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentChecks.Editor.Mcp
{
    [McpForUnityTool(
        "project_check",
        Description = "Compact project check: compile state, console errors, reference validation of build scenes and prefabs, Architector conformance, code layout, layer dependency evidence and scene guard. Returns status, new issues, known (baseline) count, stale baseline lines and per-check status.",
        RequiresPolling = true,
        PollAction = "status",
        MaxPollSeconds = 300,
        Group = "core")]
    public static class ProjectCheckTool
    {
        private static string CliProject => Path.Combine(AgentChecksPackagePaths.Root, "Tools~/ProjectCheck/ProjectCheck.csproj");
        private static readonly string[] Scopes = { "all", "code", "assets", "architecture" };
        private static AgentToolProcess _run;

        public sealed class Parameters
        {
            [ToolParameter("run starts a check; status returns the result of the last run.", Required = false, DefaultValue = "run")]
            public string action { get; set; }

            [ToolParameter("Check scope: all, code, assets or architecture.", Required = false, DefaultValue = "all")]
            public string scope { get; set; }

            [ToolParameter("Maximum new issues listed.", Required = false, DefaultValue = "20")]
            public int? limit { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var parameters = @params?.ToObject<Parameters>() ?? new Parameters();
            try
            {
                return (parameters.action ?? "run").Trim().ToLowerInvariant() switch
                {
                    "run" => Run(parameters.scope ?? "all", parameters.limit ?? 20),
                    "status" => Status(),
                    _ => new ErrorResponse("unsupported_action", new { message = "Valid actions: run, status." })
                };
            }
            catch (Exception exception)
            {
                return new ErrorResponse("project_check_failed", new { message = exception.Message });
            }
        }

        private static object Run(string scope, int limit)
        {
            if (!Scopes.Contains(scope))
                return new ErrorResponse("invalid_scope", new { message = "Valid scopes: " + string.Join(", ", Scopes) });
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return new ErrorResponse("editor_busy", new { message = "Unity is compiling or importing; retry when it is idle." });
            if (_run != null && !_run.HasExited)
                return new PendingResponse("project_check is already running.", 2.0);

            var root = Path.GetDirectoryName(Application.dataPath);
            var editorFile = Path.Combine(root, "Temp", "ProjectCheck", "editor.json");
            Directory.CreateDirectory(Path.GetDirectoryName(editorFile));
            File.WriteAllText(editorFile, ProjectCheckEditorChecks.Collect(scope).ToString());
            _run = AgentToolProcess.StartDotnetProject(root, CliProject, root, "--scope=" + scope, "--json", "--limit=" + limit, "--editor=" + editorFile);
            return new PendingResponse("project_check is running.", 2.0);
        }

        private static object Status()
        {
            if (_run == null)
                return new ErrorResponse("no_run", new { message = "Start a check with action run." });
            if (!_run.HasExited)
                return new PendingResponse("project_check is running.", 2.0);
            var json = _run.Output.Split('\n').FirstOrDefault(line => line.StartsWith("{"));
            if (json == null)
                return new ErrorResponse("tool_failed", new
                {
                    exit_code = _run.ExitCode,
                    error = (_run.Error + "\n" + _run.Output).Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0)
                });
            var result = JObject.Parse(json);
            return new SuccessResponse("project_check " + result["status"], result);
        }
    }
}
