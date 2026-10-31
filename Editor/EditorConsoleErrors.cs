using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace AgentChecks.Editor
{
    public static class EditorConsoleErrors
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int ErrorModes = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11) | (1 << 13) | (1 << 17) | (1 << 21) | (1 << 22);

        public static List<string> FirstLines(int limit)
        {
            var assembly = typeof(EditorWindow).Assembly;
            var entries = assembly.GetType("UnityEditor.LogEntries");
            var entryType = assembly.GetType("UnityEditor.LogEntry");
            var message = entryType.GetField("message", Members);
            var mode = entryType.GetField("mode", Members);
            var getEntry = entries.GetMethod("GetEntryInternal", Members);
            var result = new List<string>();
            var count = (int)entries.GetMethod("StartGettingEntries", Members).Invoke(null, null);
            try
            {
                var entry = Activator.CreateInstance(entryType);
                for (var i = 0; i < count && result.Count < limit; i++)
                {
                    if (!(bool)getEntry.Invoke(null, new[] { i, entry }) || ((int)mode.GetValue(entry) & ErrorModes) == 0)
                        continue;
                    var line = ((string)message.GetValue(entry)).Split('\n')[0].Trim();
                    if (!result.Contains(line))
                        result.Add(line);
                }
            }
            finally
            {
                entries.GetMethod("EndGettingEntries", Members).Invoke(null, null);
            }
            return result;
        }
    }
}
