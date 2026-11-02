using System.Collections.Generic;
using System.Linq;

namespace ModTestRunner
{
    // The runner's log lines about a mod's tests, tagged "[<mod>.Tests]", so a mod's lines can be found by that tag,
    // whichever part of a run wrote them. Lines about no one mod (a mistyped command) go untagged.
    internal static class RunLog
    {
        public static string Tag(ModTests mod) => mod != null ? $"[{mod.Name}.Tests]" : "";

        // The tags of every mod among the scenarios, for a line about all of them.
        public static string Tags(IEnumerable<Scenario> scenarios) =>
            string.Join(" ", scenarios.Select(scenario => scenario.Mod).Distinct().Select(Tag));

        public static void Info(string tag, string message) => ModTestRunnerPlugin.Log.LogInfo(Line(tag, message));

        public static void Warning(string tag, string message) => ModTestRunnerPlugin.Log.LogWarning(Line(tag, message));

        public static void Error(string tag, string message) => ModTestRunnerPlugin.Log.LogError(Line(tag, message));

        private static string Line(string tag, string message) => string.IsNullOrEmpty(tag) ? message : $"{tag} {message}";
    }
}
