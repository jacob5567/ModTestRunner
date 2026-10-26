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
                ModTestRunnerPlugin.Log.LogWarning($"Tests for {tests.Name} are already registered.");
                return;
            }
            mods.Add(tests);
            ModTestRunnerPlugin.Log.LogInfo($"Registered the tests for {tests.Name}.");
        }

        internal static ModTests Find(string name)
        {
            return mods.FirstOrDefault(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Every registered scenario, in run order. A mod whose scenarios can't be listed is left out, with an error.
        internal static List<Scenario> Scenarios()
        {
            List<Scenario> all = new List<Scenario>();
            foreach (ModTests mod in mods)
            {
                try
                {
                    foreach (Scenario scenario in mod.Scenarios())
                    {
                        scenario.Mod = mod;
                        all.Add(scenario);
                    }
                }
                catch (Exception exception)
                {
                    all.RemoveAll(scenario => scenario.Mod == mod);
                    ModTestRunnerPlugin.Log.LogError($"Could not list the scenarios of {mod.Name}: {exception}");
                }
            }
            return ScenarioOrder.Sort(all);
        }
    }
}
