using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ModTestRunner
{
    // A failed check. Ends the scenario.
    public sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    // The scenario can't run here (what it tests is already done, the content is missing, ...). Not a failure.
    public sealed class ScenarioSkipped : Exception
    {
        public ScenarioSkipped(string message) : base(message) { }
    }

    // One in-game test. Run is a coroutine that sets the situation up, then checks the result. Subclass it to carry what
    // a mod's Precondition needs to know.
    public class Scenario
    {
        // Phases order the run: lower phases first. A mod uses them to keep scenarios that change a stage (starting its
        // teleporter, killing its boss) after the ones that need it untouched.
        public readonly int Phase;
        // A group of related scenarios, picked with mod_test --suite.
        public readonly string Suite;
        public readonly string Name;
        // The stage to be in (SceneDef.cachedName), loaded once with next_stage and kept while the next scenarios need
        // the same one. Null runs it wherever the player is.
        public readonly string Scene;
        // Changes the run for good (earns a one-time milestone, say), so it runs after everything else.
        public readonly bool RunsLast;
        public readonly Func<Harness, IEnumerator> Run;

        // The mod that registered it, set by TestRegistry.
        public ModTests Mod { get; internal set; }

        public string FullName => $"{Mod?.Name}.{Suite}.{Name}";

        public Scenario(int phase, string suite, string name, string scene, Func<Harness, IEnumerator> run, bool runsLast = false)
        {
            Phase = phase;
            Suite = suite;
            Name = name;
            Scene = scene;
            Run = run;
            RunsLast = runsLast;
        }
    }

    // The run order: scenarios that run last at the end, then by phase, then grouped by stage. Within a stage they keep
    // their file order, so a scenario that changes a stage goes after the others.
    internal static class ScenarioOrder
    {
        public static List<Scenario> Sort(List<Scenario> list)
        {
            // Within a phase, stages run in the order they first appear, so each is loaded once.
            Dictionary<string, int> firstSeen = new Dictionary<string, int>();
            foreach (Scenario s in list)
            {
                string key = StageKey(s);
                if (!firstSeen.ContainsKey(key))
                {
                    firstSeen[key] = firstSeen.Count;
                }
            }
            return list.OrderBy(s => s.RunsLast).ThenBy(s => s.Phase).ThenBy(s => firstSeen[StageKey(s)]).ToList();
        }

        private static string StageKey(Scenario s)
        {
            return $"{s.RunsLast}/{s.Phase}/{s.Scene}";
        }
    }
}
