using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentChecks.Editor
{
    public static class AgentToolTargets
    {
        public static string Path(Transform transform)
        {
            var path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }

        public static string Describe(GameObject gameObject)
        {
            var context = gameObject.scene.IsValid() ? gameObject.scene.name : AssetDatabase.GetAssetPath(gameObject.transform.root.gameObject);
            return context + ": " + Path(gameObject.transform);
        }

        public static IEnumerable<Scene> LoadedScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    yield return scene;
            }
        }

        public static bool TryResolve(string target, out List<GameObject> roots, out string error)
        {
            roots = new List<GameObject>();
            error = null;
            if (string.IsNullOrWhiteSpace(target))
            {
                roots.AddRange(LoadedScenes().SelectMany(scene => scene.GetRootGameObjects()));
                return true;
            }

            var separator = target.IndexOf('#');
            var assetPath = separator < 0 ? target : target[..separator];
            if (assetPath.EndsWith(".prefab"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null)
                {
                    error = "Prefab not found: " + assetPath;
                    return false;
                }
                if (separator < 0)
                {
                    roots.Add(prefab);
                    return true;
                }
                var child = prefab.GetComponentsInChildren<Transform>(true).FirstOrDefault(transform => Path(transform) == target[(separator + 1)..]);
                if (child != null)
                    roots.Add(child.gameObject);
                else
                    error = "No object at " + target[(separator + 1)..] + " in " + assetPath;
                return child != null;
            }

            var scene = LoadedScenes().FirstOrDefault(candidate => candidate.name == target || candidate.path == target);
            if (scene.IsValid())
            {
                roots.AddRange(scene.GetRootGameObjects());
                return true;
            }

            var sceneSeparator = target.IndexOf(':');
            var sceneName = sceneSeparator < 0 ? null : target[..sceneSeparator];
            var objectPath = sceneSeparator < 0 ? target : target[(sceneSeparator + 1)..].Trim();
            roots.AddRange(LoadedScenes().Where(candidate => sceneName == null || candidate.name == sceneName)
                .SelectMany(candidate => candidate.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Where(transform => Path(transform) == objectPath)
                .Select(transform => transform.gameObject));
            if (roots.Count == 0)
                error = "No loaded scene, scene object or prefab matches " + target + ". Use a scene name, [Scene:]hierarchy/path or Assets/...prefab[#child/path].";
            return roots.Count > 0;
        }
    }
}
