using System;
using System.Collections.Generic;
using System.Linq;

namespace ModTestRunner
{
    // One part of a run: its scenarios, in run order, after the runner gives it a clean start (Harness).
    internal sealed class RunPart
    {
        public readonly string Name;
        public readonly List<Scenario> Scenarios;

        public RunPart(string name, List<Scenario> scenarios)
        {
            Name = name;
            Scenarios = scenarios;
        }
    }

    // How the picked scenarios run: suite by suite, in the mods' and their suites' order, each scenario once, with the
    // first suite that holds it (among those named with --suite, if any). Then the scenarios in no such suite, then the
    // ones that run last, after everything else, since they change the run for good.
    internal static class RunPlan
    {
        private const string NoSuite = "(no suite)";
        private const string Last = "(run last)";

        public static List<RunPart> Split(List<Scenario> picked, List<string> namedSuites)
        {
            List<RunPart> parts = new List<RunPart>();
            List<Scenario> ordinary = picked.Where(s => !s.RunsLast).ToList();
            HashSet<Scenario> placed = new HashSet<Scenario>();
            List<Suite> suites = ordinary.SelectMany(s => s.Suites).Distinct()
                .Where(suite => namedSuites.Count == 0 || namedSuites.Any(name => Names(suite, name)))
                .OrderBy(suite => TestRegistry.IndexOf(suite.Mod)).ThenBy(suite => suite.Order).ToList();
            foreach (Suite suite in suites)
            {
                List<Scenario> part = ordinary.Where(s => !placed.Contains(s) && s.Suites.Contains(suite)).ToList();
                if (part.Count == 0) continue;
                placed.UnionWith(part);
                parts.Add(new RunPart($"{suite.Mod.Name}.{suite.Name}", part));
            }
            List<Scenario> rest = ordinary.Where(s => !placed.Contains(s)).ToList();
            if (rest.Count > 0)
            {
                parts.Add(new RunPart(NoSuite, rest));
            }
            List<Scenario> last = picked.Where(s => s.RunsLast).ToList();
            if (last.Count > 0)
            {
                parts.Add(new RunPart(Last, last));
            }
            return parts;
        }

        private static bool Names(Suite suite, string name)
        {
            return string.Equals(suite.Name, name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals($"{suite.Mod.Name}.{suite.Name}", name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
