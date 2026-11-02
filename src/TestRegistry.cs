using System;
using System.Collections.Generic;
using System.Linq;

namespace ModTestRunner
{
    // The mods whose test plugins registered, in registration order.
    public static class TestRegistry
    {
        private static readonly List<ModTests> mods = new List<ModTests>();

        public static IReadOnlyList<ModTests> Mods => mods;

        // Call from the test plugin's Awake. The plugin needs [BepInDependency(ModTestRunnerPlugin.PluginGUID)], so the
        // runner has loaded first.
        public static void Register(ModTests tests)
        {
            if (tests == null) throw new ArgumentNullException(nameof(tests));
            if (Find(tests.Name) != null)
            {
                RunLog.Warning(RunLog.Tag(tests), "These tests are already registered.");
                return;
            }
            mods.Add(tests);
            RunLog.Info(RunLog.Tag(tests), $"Registered the tests for {tests.Name}.");
        }

        internal static int IndexOf(ModTests mod)
        {
            return mods.IndexOf(mod);
        }

        internal static ModTests Find(string name)
        {
            return mods.FirstOrDefault(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Every registered scenario, in run order, each knowing its mod and the suites that list it. A mod's scenarios
        // are its list (ModTests.Scenarios) and its suites' scenarios, each name once. A mod whose scenarios or suites
        // can't be listed is left out, with an error.
        internal static List<Scenario> Scenarios()
        {
            List<Scenario> all = new List<Scenario>();
            foreach (ModTests mod in mods)
            {
                try
                {
                    all.AddRange(ScenariosOf(mod));
                }
                catch (Exception exception)
                {
                    RunLog.Error(RunLog.Tag(mod), $"Could not list the scenarios of {mod.Name}: {exception}");
                }
            }
            return ScenarioOrder.Sort(all);
        }

        private static List<Scenario> ScenariosOf(ModTests mod)
        {
            List<Suite> suites = mod.Suites().ToList();
            List<Scenario> scenarios = new List<Scenario>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Scenario scenario in mod.Scenarios().Concat(suites.SelectMany(suite => suite.Scenarios)))
            {
                if (names.Add(scenario.Name))
                {
                    scenarios.Add(scenario);
                }
            }
            foreach (IGrouping<string, Suite> repeated in suites.GroupBy(suite => suite.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                RunLog.Warning(RunLog.Tag(mod), $"{mod.Name} has {repeated.Count()} suites called '{repeated.Key}'.");
            }
            for (int i = 0; i < suites.Count; i++)
            {
                suites[i].Mod = mod;
                suites[i].Order = i;
            }
            foreach (Scenario scenario in scenarios)
            {
                scenario.Mod = mod;
                scenario.Suites = suites.Where(suite => suite.Holds(scenario)).ToList().AsReadOnly();
            }
            return scenarios;
        }
    }
}
