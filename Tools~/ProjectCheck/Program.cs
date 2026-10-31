using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class Program
{
    private static readonly string[] Scopes = { "all", "code", "assets", "architecture", "editor" };

    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Directory.GetCurrentDirectory());
        if (args.Contains("--test"))
            return RunTests(root);
        string? Option(string name) => args.LastOrDefault(arg => arg.StartsWith("--" + name + "="))?[(name.Length + 3)..];
        var scope = Option("scope") ?? "all";
        if (!Scopes.Contains(scope))
        {
            Console.Error.WriteLine("--scope must be one of: " + string.Join(", ", Scopes));
            return 2;
        }
        var limit = int.TryParse(Option("limit"), out var parsedLimit) ? parsedLimit : 20;

        var checks = new List<Check>();
        var runs = new List<Task<Check>>();
        var config = LoadConfig(root);
        foreach (var command in config?["commands"]?.AsArray() ?? new JsonArray())
            if (scope == "all" || scope == (string?)command!["scope"])
                runs.Add(Task.Run(() => RunCommand(root, command!)));
        if (scope is "all" or "assets")
            runs.Add(Task.Run(() => SceneGuard(root)));
        var notes = new List<string>();
        var editor = Option("editor");
        if (editor != null)
            checks.AddRange(EditorChecks(File.ReadAllText(editor), notes));
        checks.AddRange(runs.Select(run => run.Result));
        checks = checks.OrderBy(check => check.Name, StringComparer.Ordinal).ToList();

        var baselinePath = Option("baseline") ?? Path.Combine(root, (string?)config?["baseline"] ?? "agent-checks-baseline.txt");
        var baseline = File.Exists(baselinePath)
            ? File.ReadAllLines(baselinePath).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var check in checks)
        {
            var found = check.Errors.Concat(check.Warnings).ToHashSet(StringComparer.Ordinal);
            check.Known = found.Count(baseline.Contains);
            check.Errors.RemoveAll(baseline.Contains);
            check.Warnings.RemoveAll(baseline.Contains);
            if (check.Failure == null)
                stale.AddRange(baseline.Where(line => line.StartsWith(check.Name + ":", StringComparison.Ordinal) && !found.Contains(line)));
        }
        stale.Sort(StringComparer.Ordinal);

        var newIssues = checks.SelectMany(check => check.Failure != null ? new[] { check.Failure } : check.Errors.ToArray())
            .Concat(checks.SelectMany(check => check.Warnings)).ToList();
        var failed = stale.Count > 0 || checks.Any(check => check.Failure != null || check.Errors.Count > 0);
        var status = failed ? "fail" : newIssues.Count > 0 ? "warn" : "ok";
        var known = checks.Sum(check => check.Known);
        var truncated = newIssues.Count > limit;

        if (args.Contains("--json"))
        {
            var json = new
            {
                status,
                scope,
                @new = newIssues.Take(limit),
                known,
                stale,
                truncated,
                checks = checks.ToDictionary(check => check.Name, check => new
                {
                    status = check.Status,
                    @new = check.Errors.Count + check.Warnings.Count + (check.Failure != null ? 1 : 0),
                    known = check.Known
                }),
                notes
            };
            Console.WriteLine(JsonSerializer.Serialize(json, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        else
        {
            var text = new StringBuilder();
            text.AppendLine($"Project check ({scope}): {status}. {newIssues.Count} new, {known} known, {stale.Count} stale{(truncated ? $", showing {limit}" : "")}.");
            text.AppendLine("  " + string.Join(", ", checks.Select(check => $"{check.Name} {check.Status}" + (check.Known > 0 ? $" ({check.Known} known)" : ""))));
            foreach (var issue in newIssues.Take(limit))
                text.AppendLine("new: " + issue);
            foreach (var line in stale)
                text.AppendLine("stale baseline line (delete it): " + line);
            foreach (var note in notes)
                text.AppendLine("note: " + note);
            Console.Write(text.ToString());
        }
        return failed ? 1 : 0;
    }

    private static int RunTests(string root)
    {
        var failures = 0;
        var console = Console.Out;
        foreach (var directory in Directory.GetDirectories(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Tests"))).Order(StringComparer.Ordinal))
        {
            var output = new StringWriter();
            Console.SetOut(output);
            Main(new[] { root, "--scope=editor", "--editor=" + Path.Combine(directory, "editor.json"), "--baseline=" + Path.Combine(directory, "baseline.txt") });
            Console.SetOut(console);
            var actual = output.ToString().TrimEnd('\n').Split('\n');
            var expected = File.ReadAllLines(Path.Combine(directory, "expected.txt"));
            var passed = actual.SequenceEqual(expected);
            Console.WriteLine((passed ? "PASS " : "FAIL ") + Path.GetFileName(directory));
            if (passed)
                continue;
            failures++;
            foreach (var line in expected.Except(actual))
                Console.WriteLine("  missing: " + line);
            foreach (var line in actual.Except(expected))
                Console.WriteLine("  unexpected: " + line);
        }
        return failures == 0 ? 0 : 1;
    }

    private static JsonNode? LoadConfig(string root)
    {
        var path = Path.Combine(root, "agent-checks.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    private static Check RunCommand(string root, JsonNode command)
    {
        var name = (string)command["name"]!;
        var project = (string)command["project"]!;
        return (string?)command["format"] == "dependency-evidence"
            ? Dependency(root, name, project)
            : Lines(root, name, project, (string)command["summaryPrefix"]!);
    }

    private static Check Lines(string root, string name, string project, string summaryPrefix)
    {
        var check = new Check(name);
        var result = Run(root, project, root);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        if (!lines.Any(line => line.StartsWith(summaryPrefix, StringComparison.Ordinal)))
            return check.Fail(result);
        check.Errors.AddRange(lines.Where(line => !line.StartsWith(summaryPrefix, StringComparison.Ordinal)).Select(line => name + ": " + line));
        return check;
    }

    private static Check Dependency(string root, string name, string project)
    {
        var check = new Check(name);
        var jsonPath = Path.GetTempFileName();
        try
        {
            var result = Run(root, project, root, "--json=" + jsonPath);
            var text = File.ReadAllText(jsonPath);
            if (text.Length == 0)
                return check.Fail(result);
            var json = JsonNode.Parse(text)!;
            check.Errors.AddRange(json["violations"]!.AsArray().Select(item => $"{name}: {item!["sourceLayer"]} -> {item["targetLayer"]}"));
            check.Warnings.AddRange(json["warnings"]!.AsArray().Select(item => name + ": " + item));
            return check;
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    private static Check SceneGuard(string root)
    {
        var check = new Check("scene_guard");
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SceneGuard/SceneGuard.csproj"));
        var result = Run(root, project, root, "--json", "--limit=100000");
        var line = result.Output.Split('\n').FirstOrDefault(text => text.StartsWith('{'));
        if (line == null)
            return check.Fail(result);
        foreach (var issue in JsonNode.Parse(line)!["new"]!.AsArray().Select(item => item!.ToString()))
            (issue.StartsWith("scene_guard: damage ", StringComparison.Ordinal) ? check.Errors : check.Warnings).Add(issue);
        return check;
    }

    private static IEnumerable<Check> EditorChecks(string text, List<string> notes)
    {
        var json = JsonNode.Parse(text)!;
        notes.AddRange(json["notes"]?.AsArray().Select(item => item!.ToString()) ?? Enumerable.Empty<string>());
        var errors = json["errors"]?.AsArray().Select(item => item!.ToString()).ToArray() ?? Array.Empty<string>();
        var warnings = json["warnings"]?.AsArray().Select(item => item!.ToString()).ToArray() ?? Array.Empty<string>();
        foreach (var name in json["ran"]!.AsArray().Select(item => item!.ToString()))
        {
            var check = new Check(name);
            check.Errors.AddRange(errors.Where(line => line.StartsWith(name + ":", StringComparison.Ordinal)));
            check.Warnings.AddRange(warnings.Where(line => line.StartsWith(name + ":", StringComparison.Ordinal)));
            yield return check;
        }
    }

    private static (int ExitCode, string Output, string Error) Run(string root, string project, params string[] arguments)
    {
        var dotnet = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..")),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(File.Exists(dotnet) ? dotnet : "dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "run", "--project", ResolveProject(root, project), "--" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }

    private static string ResolveProject(string root, string project)
    {
        var path = Path.Combine(root, project);
        var parts = project.Replace('\\', '/').Split('/');
        if (File.Exists(path) || parts.Length < 3 || parts[0] != "Packages")
            return path;
        var cache = Path.Combine(root, "Library", "PackageCache");
        var installed = Directory.Exists(cache) ? Directory.GetDirectories(cache, parts[1] + "@*").FirstOrDefault() : null;
        return installed == null ? path : Path.Combine(new[] { installed }.Concat(parts.Skip(2)).ToArray());
    }

    private sealed class Check
    {
        public Check(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public string? Failure { get; private set; }
        public int Known { get; set; }
        public string Status => Failure != null ? "failed" : Errors.Count > 0 ? "fail" : Warnings.Count > 0 ? "warn" : "ok";

        public Check Fail((int ExitCode, string Output, string Error) result)
        {
            var tail = (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).LastOrDefault() ?? string.Empty;
            Failure = $"{Name}: tool failed (exit {result.ExitCode}): {tail}";
            return this;
        }
    }
}
