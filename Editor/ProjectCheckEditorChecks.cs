using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentChecks.Editor
{
    public static class ProjectCheckEditorChecks
    {
        private const int ConsoleLimit = 50;

        public static JObject Collect(string scope)
        {
            var ran = new List<string>();
            var results = new ProjectCheckResults();
            foreach (var check in ProjectChecks().Where(check => scope == "all" || check.Scope == scope))
            {
                ran.Add(check.Name);
                check.Run(results);
            }
            if (scope is "all" or "code")
            {
                ran.Add("compile");
                if (EditorUtility.scriptCompilationFailed)
                    results.Errors.Add("compile: script compilation failed");
                ran.Add("console");
                results.Warnings.AddRange(EditorConsoleErrors.FirstLines(ConsoleLimit)
                    .Where(line => !results.IgnoredConsolePrefixes.Any(line.StartsWith))
                    .Select(line => "console: " + line));
            }
            return new JObject
            {
                ["ran"] = new JArray(ran),
                ["errors"] = new JArray(results.Errors),
                ["warnings"] = new JArray(results.Warnings),
                ["notes"] = new JArray(results.Notes)
            };
        }

        private static IEnumerable<IProjectEditorCheck> ProjectChecks()
        {
            return TypeCache.GetTypesDerivedFrom<IProjectEditorCheck>()
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .Select(type => (IProjectEditorCheck)Activator.CreateInstance(type));
        }
    }
}
