using AgentChecks.Editor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentChecks.Editor.Mcp
{
    [McpForUnityTool(
        "hierarchy_summary",
        Description = "Compact hierarchy of loaded scenes, a scene object or a prefab asset: names to a depth with component type names, Unit and IdentifierComponent markers, inactive and prefab-instance flags. Never serializes component properties. Read-only.",
        Group = "core")]
    public static class HierarchySummaryTool
    {
        public sealed class Parameters
        {
            [ToolParameter("Scene name or path, [Scene:]hierarchy/path, or Assets/...prefab[#child/path]; empty for all loaded scenes.", Required = false)]
            public string target { get; set; }

            [ToolParameter("Levels below each root.", Required = false, DefaultValue = "2")]
            public int? depth { get; set; }

            [ToolParameter("List every object with a component whose type name contains this text, by full path, ignoring depth.", Required = false)]
            public string component { get; set; }

            [ToolParameter("Maximum rows.", Required = false, DefaultValue = "100")]
            public int? limit { get; set; }

            [ToolParameter("Collapse three or more childless siblings with the same base name and components into one 'Name ×N' row.", Required = false, DefaultValue = "true")]
            public bool? collapse { get; set; }
        }

        private const int CollapseThreshold = 3;
        private static readonly IHierarchyLabelProvider[] Providers = TypeCache.GetTypesDerivedFrom<IHierarchyLabelProvider>()
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Select(type => (IHierarchyLabelProvider)Activator.CreateInstance(type)).ToArray();
        private static readonly Regex Numbered = new(@"\s*(\(\d+\)|\d+)$");

        public static object HandleCommand(JObject @params)
        {
            var parameters = @params?.ToObject<Parameters>() ?? new Parameters();
            try
            {
                if (!AgentToolTargets.TryResolve(parameters.target, out var roots, out var error))
                    return new ErrorResponse("target_not_found", new { message = error });
                var rows = new List<string>();
                if (string.IsNullOrWhiteSpace(parameters.component))
                    foreach (var root in roots)
                        Walk(root.transform, 0, Math.Max(0, parameters.depth ?? 2), parameters.collapse ?? true, rows);
                else
                    rows.AddRange(roots.SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                        .Where(transform => transform.GetComponents<Component>().Any(component => component != null &&
                            component.GetType().Name.IndexOf(parameters.component, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Select(transform => AgentToolTargets.Describe(transform.gameObject) + Label(transform.gameObject)));
                var limit = Math.Max(1, parameters.limit ?? 100);
                return new SuccessResponse($"{rows.Count} rows.", new { rows = rows.Take(limit), count = rows.Count, truncated = rows.Count > limit });
            }
            catch (Exception exception)
            {
                return new ErrorResponse("hierarchy_summary_failed", new { message = exception.Message });
            }
        }

        private static void Walk(Transform transform, int level, int depth, bool collapse, List<string> rows)
        {
            var line = new string(' ', level * 2) + transform.name + Label(transform.gameObject);
            if (level == depth && transform.childCount > 0)
                line += $" (+{transform.childCount} children)";
            rows.Add(line);
            if (level >= depth)
                return;
            var children = transform.Cast<Transform>().ToList();
            var groups = collapse
                ? children.Where(child => child.childCount == 0)
                    .GroupBy(child => BaseName(child.name) + Label(child.gameObject))
                    .Where(group => group.Count() >= CollapseThreshold).ToList()
                : new List<IGrouping<string, Transform>>();
            var collapsed = groups.SelectMany(group => group.Skip(1)).ToHashSet();
            var heads = groups.ToDictionary(group => group.First(), group => group.Count());
            foreach (var child in children)
            {
                if (collapsed.Contains(child))
                    continue;
                if (heads.TryGetValue(child, out var count))
                    rows.Add(new string(' ', (level + 1) * 2) + BaseName(child.name) + " ×" + count + Label(child.gameObject));
                else
                    Walk(child, level + 1, depth, collapse, rows);
            }
        }

        private static string LabelOf(Component component)
        {
            foreach (var provider in Providers)
                if (provider.TryLabel(component, out var label))
                    return label;
            return component.GetType().Name;
        }

        private static string BaseName(string name)
        {
            var baseName = Numbered.Replace(name, string.Empty);
            return baseName.Length > 0 ? baseName : name;
        }

        private static string Label(GameObject gameObject)
        {
            var parts = new List<string>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                switch (component)
                {
                    case null:
                        parts.Add("Missing Script");
                        break;
                    case Transform:
                        break;
                    default:
                        parts.Add(LabelOf(component));
                        break;
                }
            }
            var flags = (gameObject.activeSelf ? "" : " inactive") +
                        (PrefabUtility.IsAnyPrefabInstanceRoot(gameObject) ? " prefab:" + System.IO.Path.GetFileNameWithoutExtension(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject)) : "");
            return (parts.Count > 0 ? " [" + string.Join(", ", parts) + "]" : "") + flags;
        }
    }
}
