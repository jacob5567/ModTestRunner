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
    // a mod's Precondition needs to know. Suites (Suite) list the scenarios they hold; a scenario can be in any number
    // of them, or none.
    public class Scenario
    {
        // Phases order a suite's scenarios: lower phases first. A mod uses them to keep scenarios that change a stage
        // (starting its teleporter, killing its boss) after the ones that need it untouched.
        public readonly int Phase;
        // Unique among the mod's scenarios: what mod_test --test picks, and what suites list it by.
        public readonly string Name;
        // The stage to be in (SceneDef.cachedName), loaded once with next_stage and kept while the next scenarios need
        // the same one. Null runs it wherever the player is.
        public readonly string Scene;
        // Changes the run for good (earns a one-time milestone, say), so it runs after every suite (RunPlan).
        public readonly bool RunsLast;
        public readonly Func<Harness, IEnumerator> Run;

        // The mod that registered it, and the suites that list it, set by TestRegistry.
        public ModTests Mod { get; internal set; }
        public IReadOnlyList<Suite> Suites { get; internal set; } = new Suite[0];

        // Runs only when named with --suite or --test, not in a plain mod_test or mod_test --mod: when every suite that
        // lists it runs only when named.
        public bool OnlyWhenNamed => Suites.Count > 0 && Suites.All(suite => suite.OnlyWhenNamed);

        public string FullName => $"{Mod?.Name}.{Name}";

        public Scenario(int phase, string name, string scene, Func<Harness, IEnumerator> run, bool runsLast = false)
        {
            Phase = phase;
            Name = name;
            Scene = scene;
            Run = run;
            RunsLast = runsLast;
        }
    }

    // The order within each part of a run (RunPlan): scenarios that run last at the end, then by phase, then grouped by
    // stage. Within a stage they keep their file order, so a scenario that changes a stage goes after the others.
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
