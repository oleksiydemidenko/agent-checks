# Changelog

## 0.1.2

- Recognize configured component-owned camera render targets without suppressing missing target, asset or owner errors.
- Read scene guard configuration, script GUIDs and render textures from the selected Git snapshot for staged and revision checks.
- Add camera ownership and staged-reference regression checks; normalize fixture output across operating systems.

## 0.1.1

- Noted the early development status in the package description.

## 0.1.0

First public version.

- `scene_guard`: per-object scene and prefab diff with editor-damage detection and a commit hook.
- `project_check`: one compact report with a reviewed baseline and extension points for project checks.
- `hierarchy_summary`: compact scene and prefab hierarchy with extension points for labels.
- `editor_lease`: cooperative lease for agents sharing one Unity Editor.
