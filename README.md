# Agent Checks

Compact project checks for coding agents working in Unity. They report scene damage, show a hierarchy without serializing components, let agents share one Editor, and report project state in a few lines.

| Tool | Kind | Purpose |
| --- | --- | --- |
| `scene_guard` | command line | Per-object diff of scenes and prefabs, editor-damage detection, commit hook |
| `project_check` | command line, MCP | One compact report over compile state, console, your checks and scene guard, with a reviewed baseline |
| `hierarchy_summary` | MCP | Compact hierarchy of scenes and prefabs, without component serialization |
| `editor_lease` | MCP | Cooperative lease so parallel agents do not change the Editor at the same time |

## Install

Unity 6000.3 or later, and the .NET SDK for the command-line tools. MCP tools register through [MCP for Unity by Coplay](https://github.com/CoplayDev/unity-mcp). Without it, the command-line tools still work.

In Unity open **Window > Package Manager > + > Add package from git URL** and enter:

```
https://github.com/oleksiydemidenko/agent-checks.git
```

## scene_guard

`scene_guard` compares scenes and prefabs per YAML document and reports objects by hierarchy path:

```
dotnet run --project <package>/Tools~/SceneGuard -- .
```

Useful options: `--base=<rev>`, `--staged`, `--range=A..B`, `--path=<prefix>`, `--json`, `--limit=N`, `--hierarchy=<scene or prefab>`. Repair flags work on the working tree only: `--repair`, `--normalize`, `--restore=<path>#<fileID,...>`.

It reports **damage** (exit code 1) and **review** items (exit code 0). Built in: a camera that lost its target texture, components reordered, prefab modifications removed without a change to the source prefab.

To block damage at commit time, call it from a `pre-commit` hook with `--staged`.

## project_check

```
dotnet run --project <package>/Tools~/ProjectCheck -- . --scope=all
```

Scopes: `all`, `code`, `assets`, `architecture`. The MCP tool `project_check` adds the Editor checks: compile state, console errors and your own checks.

Each known issue can be accepted as one exact line in the baseline file. A baseline line that no longer matches fails the check. Agents should never add lines without a person's approval.

## Configure

Create `agent-checks.json` in the project root:

```json
{
    "baseline": "agent-checks-baseline.txt",
    "sceneGuard": {
        "requiredEnabled": [
            { "kind": "render-root-disabled", "script": "Assets/Scripts/Rendering/RenderRoot.cs" }
        ],
        "hierarchyLabels": [
            { "script": "Assets/Scripts/Core/Entity.cs", "label": "Entity", "field": "_definition", "assetRoot": "Assets/Data/" }
        ]
    },
    "commands": [
        { "name": "layout", "scope": "code", "project": "tools/Layout/Layout.csproj", "format": "lines", "summaryPrefix": "Layout:" },
        {
            "name": "dependency",
            "scope": "architecture",
            "project": "Packages/com.oleksiydemidenko.architector/Tools~/DependencyEvidence/DependencyEvidence.csproj",
            "format": "dependency-evidence"
        }
    ]
}
```

- `requiredEnabled`: a component of this script being disabled counts as damage.
- `hierarchyLabels`: show a component as `Label(asset name)`, read from a serialized asset reference.
- `commands`: your own .NET tools. With `lines`, every output line except the summary line is an issue. `dependency-evidence` reads the report of Architector's tool.

## Extend

Add project checks to the MCP run by implementing `IProjectEditorCheck` in any Editor assembly:

```csharp
public sealed class SettingsCheck : IProjectEditorCheck
{
    public string Name => "settings";
    public string Scope => "assets";

    public void Run(ProjectCheckResults results)
    {
        if (!PlayerSettings.runInBackground)
            results.Errors.Add("settings: Run In Background is off");
    }
}
```

Start each issue with the check's name, so that baselines work. Implement `IHierarchyLabelProvider` to give `hierarchy_summary` your own labels.

## editor_lease

Several agents share one Editor. Before saving scenes, running tests or changing scripts, an agent acquires the lease and releases it when done. Read-only tools need no lease. It is a convention that every agent follows, not an enforcement, so tell your agents about it:

```markdown
Before execute_code, a scene or prefab save, an asset refresh, a script change, a test run or Play,
acquire `editor_lease` with a stable client name and release it when done. On `busy`, wait or report the holder.
```

## Status

Agent Checks is in development. It is developed on Unity 6000.3.16f1 on Linux. Issues are welcome.

## License

[MIT](LICENSE.md)
