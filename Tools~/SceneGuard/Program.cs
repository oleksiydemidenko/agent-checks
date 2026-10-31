using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class Program
{
    private static readonly Regex Header = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);
    private static readonly Regex TopKey = new(@"^  ([^ \-][^:]*):", RegexOptions.Compiled);
    private static readonly Regex FileIdValue = new(@"fileID: (-?\d+)", RegexOptions.Compiled);
    private static readonly Regex GuidValue = new(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex Modification = new(@"^  - target: \{fileID: (-?\d+)[^}]*\}\n    propertyPath: (.*)\n    value: (.*)\n    objectReference: (.*)$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Directory.GetCurrentDirectory());
        var options = args.Where(arg => arg.StartsWith("--")).ToArray();
        string? Option(string name) => options.LastOrDefault(arg => arg.StartsWith("--" + name + "="))?[(name.Length + 3)..];
        if (options.Contains("--test"))
            return RunTests(root);
        var hierarchy = Option("hierarchy");
        if (hierarchy != null)
        {
            var revision = Option("revision");
            var text = revision == null ? File.ReadAllText(Path.Combine(root, hierarchy)) : GitResult(root, "show", revision + ":" + hierarchy.Replace('\\', '/')).Output;
            var rows = Hierarchy(root, new SceneFile(text), int.TryParse(Option("depth"), out var depth) ? depth : 2, Option("component"), !options.Contains("--no-collapse"));
            var rowLimit = int.TryParse(Option("limit"), out var parsedRowLimit) ? parsedRowLimit : 100;
            if (options.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(new { rows = rows.Take(rowLimit), count = rows.Count, truncated = rows.Count > rowLimit },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            else
                Console.Write(string.Concat(rows.Take(rowLimit).Select(row => row + "\n")) + (rows.Count > rowLimit ? $"(+{rows.Count - rowLimit} rows)\n" : ""));
            return 0;
        }

        var limit = int.TryParse(Option("limit"), out var parsedLimit) ? parsedLimit : 30;
        var paths = options.Where(arg => arg.StartsWith("--path=")).Select(arg => arg[7..].Replace('\\', '/')).ToArray();
        var compare = Option("compare");
        Report report;
        if (compare != null)
        {
            var pair = compare.Split(',');
            report = Analyze(root, new[] { (pair[1].Replace('\\', '/'), File.ReadAllText(pair[0]), File.ReadAllText(pair[1])) }, new HashSet<string>(), "file", "file");
        }
        else
        {
            var source = Source.From(Option("base"), Option("target"), Option("range"), options.Contains("--staged"));
            Report AnalyzeSource()
            {
                var changed = source.ChangedFiles(root);
                var scenes = changed.Where(IsSceneFile).Where(path => paths.Length == 0 || paths.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                    .Select(path => (path, source.ReadBase(root, path), source.ReadTarget(root, path))).ToArray();
                return Analyze(root, scenes, changed.ToHashSet(StringComparer.Ordinal), source.BaseLabel, source.TargetLabel);
            }
            report = AnalyzeSource();
            var restore = Option("restore");
            if (options.Contains("--repair") || restore != null || options.Contains("--normalize"))
            {
                if (!source.IsWorkingTree)
                {
                    Console.Error.WriteLine("Repair actions write the working tree; do not combine them with --target, --range or --staged.");
                    return 2;
                }
                Repair(root, report, options.Contains("--repair"), restore, options.Contains("--normalize"));
                report = AnalyzeSource();
            }
        }

        if (options.Contains("--json"))
            Console.WriteLine(report.Json(limit));
        else
            Console.Write(report.Text(limit));
        return report.Issues.Any(issue => issue.Damage) ? 1 : 0;
    }

    private static List<string> Hierarchy(string root, SceneFile file, int depth, string? component, bool collapse)
    {
        var scripts = new GuidPaths(root, ".cs.meta");
        var prefabs = new GuidPaths(root, ".prefab.meta");
        var identifiers = new GuidPaths(root, ".asset.meta");
        var labels = HierarchyLabels(root, scripts);
        string AssetName(string block, string assetRoot)
        {
            var hash = Regex.Match(block, @"(?:Hash|guid): ([0-9a-f]{32})");
            if (!hash.Success || hash.Groups[1].Value.Trim('0').Length == 0)
                return "unidentified";
            var path = identifiers.Path(hash.Groups[1].Value);
            return path == null ? "missing " + hash.Groups[1].Value : path.Replace(assetRoot, string.Empty).Replace(".asset", string.Empty);
        }
        string ComponentName(Document document)
        {
            if (document.Script == null)
                return document.ClassName;
            if (labels.TryGetValue(document.Script, out var label))
                return label.Name + "(" + AssetName(document.Block(label.Field), label.AssetRoot) + ")";
            var script = scripts.Path(document.Script);
            return script == null ? "Missing Script" : Path.GetFileNameWithoutExtension(script);
        }
        string Label(string transformId, out List<string> components, out string name)
        {
            components = new List<string>();
            var transform = file.Documents[transformId];
            if (transform.Stripped)
            {
                var instance = file.Documents.GetValueOrDefault(FileIdValue.Match(transform.Block("m_PrefabInstance")).Groups[1].Value);
                var source = instance == null ? null : prefabs.Path(GuidValue.Match(instance.Block("m_SourcePrefab")).Groups[1].Value);
                var nameMatch = instance == null ? Match.Empty : Regex.Match(instance.Block("m_Modification"), @"propertyPath: m_Name\n\s*value: (.*)");
                name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(source ?? "?");
                return name + " prefab:" + Path.GetFileNameWithoutExtension(source ?? "missing");
            }
            var gameObject = file.Documents.GetValueOrDefault(FileIdValue.Match(transform.Block("m_GameObject")).Groups[1].Value);
            name = gameObject?.Value("m_Name") ?? "?";
            if (gameObject == null)
                return name;
            components = FileIdValue.Matches(gameObject.Block("m_Component")).Select(match => match.Groups[1].Value)
                .Where(id => id != transformId).Select(id => file.Documents.TryGetValue(id, out var document) ? ComponentName(document) : "?").ToList();
            return name + (components.Count > 0 ? " [" + string.Join(", ", components) + "]" : "") + (gameObject.Value("m_IsActive") == "0" ? " inactive" : "");
        }
        IEnumerable<string> Children(string transformId)
        {
            var transform = file.Documents[transformId];
            var children = FileIdValue.Matches(transform.Block("m_Children")).Select(match => match.Groups[1].Value).Where(file.Documents.ContainsKey).ToList();
            if (transform.Stripped)
            {
                var instance = FileIdValue.Match(transform.Block("m_PrefabInstance")).Groups[1].Value;
                children.AddRange(file.Ordered.Where(document => document.ClassName == "PrefabInstance" && document.FileId != instance &&
                                                                  FileIdValue.Match(document.Block("m_Modification")).Groups[1].Value == transformId)
                    .SelectMany(document => file.Ordered.Where(candidate => candidate.Stripped && candidate.ClassName is "Transform" or "RectTransform" &&
                                                                             FileIdValue.Match(candidate.Block("m_PrefabInstance")).Groups[1].Value == document.FileId &&
                                                                             FileIdValue.Match(candidate.Block("m_Father")).Groups[1].Value is "" or "0").Take(1))
                    .Select(document => document.FileId));
            }
            return children;
        }
        var transforms = file.Ordered.Where(document => document.ClassName is "Transform" or "RectTransform").ToList();
        var sceneRoots = file.Ordered.FirstOrDefault(document => document.ClassName == "SceneRoots");
        var roots = sceneRoots != null
            ? FileIdValue.Matches(sceneRoots.Block("m_Roots")).Select(match => match.Groups[1].Value).Where(file.Documents.ContainsKey).ToList()
            : transforms.Where(document => !document.Stripped && FileIdValue.Match(document.Block("m_Father")).Groups[1].Value == "0").Select(document => document.FileId).ToList();
        var rows = new List<string>();
        void Walk(string transformId, int level, string path)
        {
            var label = Label(transformId, out var components, out var name);
            var fullPath = path.Length == 0 ? name : path + "/" + name;
            var children = Children(transformId).ToList();
            if (component == null)
                rows.Add(new string(' ', level * 2) + label + (level == depth && children.Count > 0 ? $" (+{children.Count} children)" : ""));
            else if (components.Any(item => item.Contains(component, StringComparison.OrdinalIgnoreCase)))
                rows.Add(fullPath + label[name.Length..]);
            if (component == null && level >= depth)
                return;
            var heads = new Dictionary<string, (int Count, string Row)>();
            var collapsed = new HashSet<string>();
            if (component == null && collapse)
                foreach (var group in children.Where(child => !Children(child).Any())
                             .Select(child => (Id: child, Label: Label(child, out _, out var childName), Name: childName))
                             .GroupBy(child => BaseName(child.Name) + child.Label[child.Name.Length..])
                             .Where(group => group.Count() >= CollapseThreshold))
                {
                    var first = group.First();
                    heads[first.Id] = (group.Count(), new string(' ', (level + 1) * 2) + BaseName(first.Name) + " ×" + group.Count() + first.Label[first.Name.Length..]);
                    collapsed.UnionWith(group.Skip(1).Select(child => child.Id));
                }
            foreach (var child in children.Where(child => !collapsed.Contains(child)))
                if (heads.TryGetValue(child, out var head))
                    rows.Add(head.Row);
                else
                    Walk(child, level + 1, fullPath);
        }
        foreach (var rootId in roots)
            Walk(rootId, 0, string.Empty);
        return rows;
    }

    private const int CollapseThreshold = 3;
    private static readonly Regex Numbered = new(@"\s*(\(\d+\)|\d+)$");

    private static string BaseName(string name)
    {
        var baseName = Numbered.Replace(name, string.Empty);
        return baseName.Length > 0 ? baseName : name;
    }

    private static JsonArray ConfigRules(string root, string section)
    {
        var config = Path.Combine(root, "agent-checks.json");
        return !File.Exists(config) ? new JsonArray() : JsonNode.Parse(File.ReadAllText(config))?["sceneGuard"]?[section]?.AsArray() ?? new JsonArray();
    }

    private static List<(string Kind, string Guid)> RequiredEnabledScripts(string root, GuidPaths scripts)
    {
        return ConfigRules(root, "requiredEnabled")
            .Select(rule => (Kind: (string)rule!["kind"]!, Guid: scripts.Guid((string)rule!["script"]!)))
            .Where(rule => rule.Guid != null).Select(rule => (rule.Kind, rule.Guid!)).ToList();
    }

    private static Dictionary<string, (string Name, string Field, string AssetRoot)> HierarchyLabels(string root, GuidPaths scripts)
    {
        return ConfigRules(root, "hierarchyLabels")
            .Select(rule => (Guid: scripts.Guid((string)rule!["script"]!), Name: (string)rule!["label"]!, Field: (string)rule!["field"]!, AssetRoot: (string?)rule!["assetRoot"] ?? string.Empty))
            .Where(rule => rule.Guid != null).ToDictionary(rule => rule.Guid!, rule => (rule.Name, rule.Field, rule.AssetRoot), StringComparer.Ordinal);
    }

    private static bool IsSceneFile(string path) => path.EndsWith(".unity", StringComparison.Ordinal) || path.EndsWith(".prefab", StringComparison.Ordinal);

    private static Report Analyze(string root, IEnumerable<(string Path, string Base, string Target)> files, HashSet<string> changedFiles, string baseLabel, string targetLabel)
    {
        var report = new Report(baseLabel, targetLabel);
        var scripts = new GuidPaths(root, ".cs.meta");
        var prefabs = new GuidPaths(root, ".prefab.meta");
        var requiredEnabled = RequiredEnabledScripts(root, scripts);
        foreach (var (path, baseText, targetText) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var before = new SceneFile(baseText);
            var after = new SceneFile(targetText);
            var result = new FileResult(path, before, after);
            foreach (var document in after.Documents.Values)
            {
                if (!before.Documents.TryGetValue(document.FileId, out var old))
                {
                    result.Added++;
                    result.Objects.Add($"+ {after.Describe(document, scripts)} &{document.FileId}");
                    foreach (var rule in requiredEnabled.Where(rule => document.Script == rule.Guid && document.Value("m_Enabled") == "0"))
                        report.Add(true, rule.Kind, path, document, after.Describe(document, scripts), "added with m_Enabled: 0");
                    continue;
                }
                if (old.Normalized == document.Normalized)
                {
                    if (old.Raw != document.Raw)
                        result.Whitespace.Add(document.FileId);
                    continue;
                }

                result.Changed++;
                var description = after.Describe(document, scripts);
                result.Objects.Add($"~ {description} &{document.FileId}: {ChangedKeys(old, document)}");
                if (document.ClassName == "Camera" && GuidValue.IsMatch(old.Block("m_TargetTexture")) && document.Value("m_TargetTexture") == "{fileID: 0}")
                    report.Add(true, "camera-target-texture-reset", path, document, description, "m_TargetTexture was " + GuidValue.Match(old.Block("m_TargetTexture")).Groups[1].Value);
                foreach (var rule in requiredEnabled.Where(rule => document.Script == rule.Guid && document.Value("m_Enabled") == "0" && old.Value("m_Enabled") != "0"))
                    report.Add(true, rule.Kind, path, document, description, "m_Enabled 1 -> 0");
                if (document.ClassName == "GameObject")
                {
                    var oldComponents = FileIdValue.Matches(old.Block("m_Component")).Select(match => match.Groups[1].Value).ToArray();
                    var newComponents = FileIdValue.Matches(document.Block("m_Component")).Select(match => match.Groups[1].Value).ToArray();
                    if (!oldComponents.SequenceEqual(newComponents) && oldComponents.Order().SequenceEqual(newComponents.Order()))
                        report.Add(false, "component-order", path, document, description, "same components in a different order");
                }
                if (document.ClassName == "PrefabInstance")
                {
                    var removed = Modifications(old).Keys.Except(Modifications(document).Keys).ToArray();
                    var sourceGuid = GuidValue.Match(document.Block("m_SourcePrefab")).Groups[1].Value;
                    var sourcePath = prefabs.Path(sourceGuid);
                    if (removed.Length > 0 && (sourcePath == null || !changedFiles.Contains(sourcePath)))
                        report.Add(false, "prefab-modification-removed", path, document, description,
                            "removed " + string.Join(", ", removed.Take(3).Select(key => key.Split('|')[1])) + (removed.Length > 3 ? $" (+{removed.Length - 3})" : "") + "; source prefab unchanged");
                }
            }
            foreach (var document in before.Documents.Values.Where(document => !after.Documents.ContainsKey(document.FileId)))
            {
                result.Removed++;
                result.Objects.Add($"- {before.Describe(document, scripts)} &{document.FileId}");
            }
            report.Files.Add(result);
        }
        return report;
    }

    private static string ChangedKeys(Document before, Document after)
    {
        var keys = after.Keys.Union(before.Keys).Where(key => before.Block(key) != after.Block(key)).ToList();
        if (after.ClassName == "PrefabInstance" && keys.Remove("m_Modification"))
        {
            var oldModifications = Modifications(before);
            var newModifications = Modifications(after);
            var added = newModifications.Keys.Count(key => !oldModifications.ContainsKey(key));
            var removed = oldModifications.Keys.Count(key => !newModifications.ContainsKey(key));
            var changed = newModifications.Count(pair => oldModifications.TryGetValue(pair.Key, out var value) && value != pair.Value);
            var paths = newModifications.Where(pair => !oldModifications.TryGetValue(pair.Key, out var value) || value != pair.Value).Select(pair => pair.Key)
                .Concat(oldModifications.Keys.Where(key => !newModifications.ContainsKey(key))).Select(key => key.Split('|')[1]).Distinct().Take(3);
            keys.Insert(0, $"modifications +{added} -{removed} ~{changed} ({string.Join(", ", paths)})");
        }
        return keys.Count > 6 ? string.Join(", ", keys.Take(6)) + $" (+{keys.Count - 6})" : string.Join(", ", keys);
    }

    private static Dictionary<string, string> Modifications(Document document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Modification.Matches(document.Block("m_Modification")))
            result[match.Groups[1].Value + "|" + match.Groups[2].Value] = match.Groups[3].Value + "|" + match.Groups[4].Value;
        return result;
    }

    private static void Repair(string root, Report report, bool repairDamage, string? restore, bool normalize)
    {
        var restoreIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (restore != null)
        {
            var separator = restore.LastIndexOf('#');
            restoreIds[restore[..separator].Replace('\\', '/')] = restore[(separator + 1)..].Split(',').Select(id => id.Trim().TrimStart('&')).ToHashSet();
        }
        foreach (var file in report.Files)
        {
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            if (normalize)
                foreach (var id in file.Whitespace)
                    replacements[id] = file.Before.Documents[id].Raw;
            if (repairDamage)
                foreach (var issue in report.Issues.Where(issue => issue.Damage && issue.Path == file.Path && file.Before.Documents.ContainsKey(issue.FileId)))
                {
                    var key = issue.Kind == "camera-target-texture-reset" ? "m_TargetTexture" : "m_Enabled";
                    var current = replacements.TryGetValue(issue.FileId, out var replaced) ? replaced : file.After.Documents[issue.FileId].Raw;
                    var oldLine = Regex.Match(file.Before.Documents[issue.FileId].Raw, @"(?m)^  " + key + @":.*$").Value;
                    replacements[issue.FileId] = Regex.Replace(current, @"(?m)^  " + key + @":[^\r\n]*", oldLine.TrimEnd('\r'));
                }
            if (restoreIds.TryGetValue(file.Path, out var ids))
                foreach (var id in ids.Where(file.Before.Documents.ContainsKey))
                    replacements[id] = file.Before.Documents[id].Raw;
            if (replacements.Count == 0)
                continue;
            var text = new StringBuilder(file.After.Preamble);
            foreach (var document in file.After.Ordered)
            {
                var replacement = replacements.TryGetValue(document.FileId, out var value) ? value : document.Raw;
                if (document.Raw.EndsWith('\n') != replacement.EndsWith('\n'))
                    replacement = document.Raw.EndsWith('\n') ? replacement + "\n" : replacement.TrimEnd('\n', '\r');
                text.Append(replacement);
            }
            File.WriteAllText(Path.Combine(root, file.Path), text.ToString());
            Console.WriteLine($"repaired {file.Path}: {replacements.Count} documents ({string.Join(", ", replacements.Keys.Select(id => "&" + id))})");
        }
    }

    private static int RunTests(string root)
    {
        var testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Tests"));
        var failures = 0;
        foreach (var directory in Directory.GetDirectories(testsRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            var report = Analyze(directory, new[] { ("current.yaml", File.ReadAllText(Path.Combine(directory, "base.yaml")), File.ReadAllText(Path.Combine(directory, "current.yaml"))) },
                new HashSet<string>(), "base.yaml", "current.yaml");
            var actual = report.Text(int.MaxValue).TrimEnd('\n').Split('\n');
            var expected = File.ReadAllLines(Path.Combine(directory, "expected.txt"));
            if (actual.SequenceEqual(expected))
            {
                Console.WriteLine("PASS " + name);
                continue;
            }
            failures++;
            Console.WriteLine("FAIL " + name);
            foreach (var line in expected.Except(actual))
                Console.WriteLine("  missing: " + line);
            foreach (var line in actual.Except(expected))
                Console.WriteLine("  unexpected: " + line);
        }
        return failures == 0 ? 0 : 1;
    }

    private static string Git(string root, params string[] arguments)
    {
        return GitResult(root, arguments).Output;
    }

    private static (bool Success, string Output) GitResult(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        error.Wait();
        return (process.ExitCode == 0, output);
    }

    private sealed class Source
    {
        private string baseRevision = "HEAD";
        private string? targetRevision;
        private bool staged;

        public bool IsWorkingTree => targetRevision == null && !staged;
        public string BaseLabel => baseRevision;
        public string TargetLabel => staged ? "index" : targetRevision ?? "working tree";

        public static Source From(string? baseRevision, string? targetRevision, string? range, bool staged)
        {
            var source = new Source { staged = staged };
            if (range != null)
            {
                var parts = range.Split("..");
                source.baseRevision = parts[0];
                source.targetRevision = parts[1];
                return source;
            }
            source.baseRevision = baseRevision ?? "HEAD";
            source.targetRevision = targetRevision;
            return source;
        }

        public string[] ChangedFiles(string root)
        {
            var arguments = new List<string> { "diff", "--name-only", "--no-renames", "-z" };
            if (staged)
                arguments.Add("--cached");
            arguments.Add(baseRevision);
            if (targetRevision != null)
                arguments.Add(targetRevision);
            var files = Git(root, arguments.ToArray()).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (IsWorkingTree)
                files.AddRange(Git(root, "ls-files", "--others", "--exclude-standard", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries));
            return files.Distinct().OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        public string ReadBase(string root, string path) => Show(root, baseRevision + ":" + path);

        public string ReadTarget(string root, string path)
        {
            if (staged)
                return Show(root, ":" + path);
            if (targetRevision != null)
                return Show(root, targetRevision + ":" + path);
            var file = System.IO.Path.Combine(root, path);
            return File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        }

        private static string Show(string root, string spec)
        {
            var result = GitResult(root, "show", spec);
            return result.Success ? result.Output : string.Empty;
        }
    }

    private sealed class GuidPaths
    {
        private readonly string root;
        private readonly string suffix;
        private Dictionary<string, string>? paths;

        public GuidPaths(string root, string suffix)
        {
            this.root = root;
            this.suffix = suffix;
        }

        public string? Path(string guid)
        {
            Load();
            return paths!.TryGetValue(guid, out var path) ? path : null;
        }

        public string? Guid(string assetPath)
        {
            Load();
            return paths!.FirstOrDefault(pair => pair.Value == assetPath).Key;
        }

        private void Load()
        {
            if (paths != null)
                return;
            paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var folder in new[] { "Assets", "Packages", "Library/PackageCache" }.Select(folder => System.IO.Path.Combine(root, folder)).Where(Directory.Exists))
            foreach (var meta in Directory.EnumerateFiles(folder, "*" + suffix, SearchOption.AllDirectories).Where(file => !System.IO.Path.GetRelativePath(root, file).Contains("~" + System.IO.Path.DirectorySeparatorChar)))
            {
                using var reader = new StreamReader(meta);
                for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    if (!line.StartsWith("guid: ", StringComparison.Ordinal))
                        continue;
                    paths[line[6..].Trim()] = System.IO.Path.GetRelativePath(root, meta[..^5]).Replace('\\', '/');
                    break;
                }
            }
        }
    }

    private sealed class Document
    {
        public string FileId = string.Empty;
        public string ClassName = string.Empty;
        public bool Stripped;
        public string Raw = string.Empty;
        public string Normalized = string.Empty;
        public List<string> Keys = new();
        public Dictionary<string, string> Blocks = new(StringComparer.Ordinal);
        public string? Script;

        public string Block(string key) => Blocks.TryGetValue(key, out var block) ? block : string.Empty;

        public string? Value(string key)
        {
            var block = Block(key);
            var separator = block.IndexOf(':');
            return separator < 0 ? null : block[(separator + 1)..].Split('\n')[0].Trim();
        }

        public static Document Parse(string raw)
        {
            var lines = raw.Replace("\r", string.Empty).Split('\n');
            var header = Header.Match(lines[0]);
            var document = new Document
            {
                FileId = header.Groups[2].Value,
                Stripped = header.Groups[3].Success,
                ClassName = lines.Length > 1 ? lines[1].TrimEnd(':', ' ') : string.Empty,
                Raw = raw,
                Normalized = string.Join("\n", lines.Select(line => line.TrimEnd()).Where(line => line.Length > 0))
            };
            string? key = null;
            var block = new StringBuilder();
            foreach (var line in lines.Skip(2).Select(line => line.TrimEnd()))
            {
                var match = TopKey.Match(line);
                if (match.Success)
                {
                    if (key != null)
                        document.Blocks[key] = block.ToString();
                    key = match.Groups[1].Value;
                    document.Keys.Add(key);
                    block.Clear();
                }
                if (key != null && line.Length > 0)
                    block.Append(block.Length > 0 ? "\n" : string.Empty).Append(line.Trim() == line ? line : line[2..]);
            }
            if (key != null)
                document.Blocks[key] = block.ToString();
            var script = GuidValue.Match(document.Block("m_Script"));
            document.Script = script.Success ? script.Groups[1].Value : null;
            return document;
        }
    }

    private sealed class SceneFile
    {
        public string Preamble = string.Empty;
        public List<Document> Ordered = new();
        public Dictionary<string, Document> Documents = new(StringComparer.Ordinal);

        public SceneFile(string text)
        {
            var chunks = Regex.Split(text, @"(?m)^(?=--- !u!)");
            Preamble = chunks[0].StartsWith("--- !u!", StringComparison.Ordinal) ? string.Empty : chunks[0];
            foreach (var chunk in chunks.Where(chunk => chunk.StartsWith("--- !u!", StringComparison.Ordinal)))
            {
                var document = Document.Parse(chunk);
                Ordered.Add(document);
                Documents[document.FileId] = document;
            }
        }

        public string Describe(Document document, GuidPaths scripts)
        {
            if (document.ClassName == "GameObject")
                return "GameObject '" + ObjectPath(document.FileId) + "'";
            if (document.ClassName == "PrefabInstance")
                return "PrefabInstance '" + PrefabInstanceName(document) + "'";
            var type = document.Script != null
                ? System.IO.Path.GetFileNameWithoutExtension(scripts.Path(document.Script) ?? "MonoBehaviour(" + document.Script[..8] + ")")
                : document.ClassName;
            var owner = FileIdValue.Match(document.Block("m_GameObject"));
            if (owner.Success && owner.Groups[1].Value != "0")
                return type + " on '" + ObjectPath(owner.Groups[1].Value) + "'";
            return document.Stripped ? type + " (stripped)" : type;
        }

        private string PrefabInstanceName(Document document)
        {
            var name = Regex.Match(document.Block("m_Modification"), @"propertyPath: m_Name\n\s*value: (.*)");
            return name.Success ? name.Groups[1].Value.Trim() : "prefab " + GuidValue.Match(document.Block("m_SourcePrefab")).Groups[1].Value;
        }

        private string ObjectPath(string gameObjectId)
        {
            var segments = new List<string>();
            var current = gameObjectId;
            for (var depth = 0; depth < 32 && current != "0" && Documents.TryGetValue(current, out var gameObject); depth++)
            {
                if (gameObject.ClassName != "GameObject")
                {
                    var instance = FileIdValue.Match(gameObject.Block("m_PrefabInstance")).Groups[1].Value;
                    segments.Add(Documents.TryGetValue(instance, out var prefabInstance) ? PrefabInstanceName(prefabInstance) : "?");
                    break;
                }
                segments.Add(gameObject.Value("m_Name") ?? "?");
                var transformId = FileIdValue.Matches(gameObject.Block("m_Component")).Select(match => match.Groups[1].Value)
                    .FirstOrDefault(id => Documents.TryGetValue(id, out var component) && component.ClassName is "Transform" or "RectTransform");
                if (transformId == null)
                    break;
                var father = FileIdValue.Match(Documents[transformId].Block("m_Father")).Groups[1].Value;
                if (!Documents.TryGetValue(father, out var fatherTransform))
                    break;
                current = fatherTransform.Stripped
                    ? FileIdValue.Match(fatherTransform.Block("m_PrefabInstance")).Groups[1].Value
                    : FileIdValue.Match(fatherTransform.Block("m_GameObject")).Groups[1].Value;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }
    }

    private sealed class FileResult
    {
        public string Path;
        public SceneFile Before;
        public SceneFile After;
        public int Changed;
        public int Added;
        public int Removed;
        public List<string> Whitespace = new();
        public List<string> Objects = new();

        public FileResult(string path, SceneFile before, SceneFile after)
        {
            Path = path;
            Before = before;
            After = after;
        }
    }

    private sealed class Issue
    {
        public bool Damage;
        public string Kind = string.Empty;
        public string Path = string.Empty;
        public string FileId = string.Empty;
        public string Line = string.Empty;
    }

    private sealed class Report
    {
        private readonly string baseLabel;
        private readonly string targetLabel;

        public Report(string baseLabel, string targetLabel)
        {
            this.baseLabel = baseLabel;
            this.targetLabel = targetLabel;
        }

        public List<FileResult> Files { get; } = new();
        public List<Issue> Issues { get; } = new();

        public void Add(bool damage, string kind, string path, Document document, string description, string detail)
        {
            Issues.Add(new Issue
            {
                Damage = damage,
                Kind = kind,
                Path = path,
                FileId = document.FileId,
                Line = $"scene_guard: {(damage ? "damage" : "review")} {kind} {path} &{document.FileId} {description}: {detail}"
            });
        }

        public string Status => Issues.Any(issue => issue.Damage) ? "fail" : Issues.Count > 0 ? "warn" : "ok";

        public string Text(int limit)
        {
            var text = new StringBuilder();
            var shown = 0;
            foreach (var file in Files)
            {
                text.AppendLine($"{file.Path} ({baseLabel} -> {targetLabel}): {file.Changed} changed, {file.Added} added, {file.Removed} removed, {file.Whitespace.Count} whitespace-only");
                foreach (var line in file.Objects.Take(Math.Max(0, limit - shown)))
                    text.AppendLine("  " + line);
                shown += Math.Min(file.Objects.Count, Math.Max(0, limit - shown));
            }
            foreach (var issue in Issues.Take(limit))
                text.AppendLine(issue.Line);
            var objects = Files.Sum(file => file.Objects.Count);
            var truncated = objects > limit || Issues.Count > limit;
            text.AppendLine($"Scene guard: {Files.Count} files, {objects} changed objects, {Issues.Count(issue => issue.Damage)} damage, {Issues.Count(issue => !issue.Damage)} review{(truncated ? $", truncated to {limit}" : "")}.");
            return text.ToString();
        }

        public string Json(int limit)
        {
            var objects = Files.Sum(file => file.Objects.Count);
            var shown = 0;
            return JsonSerializer.Serialize(new
            {
                status = Status,
                @new = Issues.Take(limit).Select(issue => issue.Line),
                known = 0,
                truncated = objects > limit || Issues.Count > limit,
                files = Files.Select(file =>
                {
                    var take = Math.Max(0, limit - shown);
                    shown += Math.Min(file.Objects.Count, take);
                    return new { path = file.Path, changed = file.Changed, added = file.Added, removed = file.Removed, whitespace = file.Whitespace.Count, objects = file.Objects.Take(take) };
                }).ToArray()
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }
}
