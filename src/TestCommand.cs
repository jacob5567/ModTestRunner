using System;
using System.Collections.Generic;
using System.Linq;
using RoR2;

namespace ModTestRunner
{
    // mod_test: picks scenarios by mod and suite, then lists or runs them. Every flag can be given more than once.
    internal static class TestCommand
    {
        public const string HelpText = "Runs registered in-game mod tests. mod_test [list] [--mod <mod>] [--suite <suite>]. "
            + "mod_test help for more.";

        private static readonly string[] Usage =
        {
            "mod_test                          runs every registered mod's scenarios",
            "mod_test --mod <mod>              runs one mod's scenarios (-m)",
            "mod_test --suite <suite>          runs one suite (-s); write <mod>.<suite> when two mods have a suite of that name",
            "mod_test --mod <mod> --suite <s>  runs that mod's suite",
            "mod_test list [...]               lists what the same flags would run, instead of running it",
        };

        public static void Run(ConCommandArgs args)
        {
            bool list = false;
            List<string> mods = new List<string>();
            List<string> suites = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                string flag = arg.ToLowerInvariant();
                switch (flag)
                {
                    case "list":
                        list = true;
                        break;
                    case "help":
                        LogUsage();
                        return;
                    case "--mod":
                    case "-m":
                    case "--suite":
                    case "-s":
                        if (i + 1 >= args.Count)
                        {
                            ModTestRunnerPlugin.Log.LogWarning($"{arg} needs a name. See mod_test help.");
                            return;
                        }
                        (flag == "--mod" || flag == "-m" ? mods : suites).Add(args[++i]);
                        break;
                    default:
                        ModTestRunnerPlugin.Log.LogWarning($"Unknown argument '{arg}'. See mod_test help.");
                        return;
                }
            }

            List<Scenario> all = TestRegistry.Scenarios();
            if (all.Count == 0)
            {
                ModTestRunnerPlugin.Log.LogWarning("No mod has registered any tests.");
                return;
            }

            List<Scenario> picked = Pick(all, mods, suites, out string error);
            if (error != null)
            {
                ModTestRunnerPlugin.Log.LogWarning(error);
                return;
            }

            if (list)
            {
                List(picked);
            }
            else
            {
                Harness.Start(picked);
            }
        }

        // The scenarios of the named mods and suites, still in run order, or null and why.
        private static List<Scenario> Pick(List<Scenario> all, List<string> mods, List<string> suites, out string error)
        {
            error = null;
            foreach (string mod in mods)
            {
                if (TestRegistry.Find(mod) == null)
                {
                    error = $"No mod called '{mod}' has registered tests. Registered: "
                        + string.Join(", ", TestRegistry.Mods.Select(m => m.Name)) + ".";
                    return null;
                }
            }
            List<Scenario> inMods = mods.Count == 0 ? all : all.Where(s => mods.Any(mod => Same(s.Mod.Name, mod))).ToList();
            if (suites.Count == 0) return inMods;

            HashSet<Scenario> inSuites = new HashSet<Scenario>();
            foreach (string suite in suites)
            {
                List<Scenario> matched = inMods.Where(s => Same(s.Suite, suite) || Same($"{s.Mod.Name}.{s.Suite}", suite)).ToList();
                if (matched.Count == 0)
                {
                    error = $"No suite called '{suite}'" + (mods.Count > 0 ? $" in {string.Join(", ", mods)}" : "") + ". Use mod_test list.";
                    return null;
                }
                List<string> owners = matched.Select(s => s.Mod.Name).Distinct().ToList();
                if (owners.Count > 1)
                {
                    error = $"{string.Join(" and ", owners)} each have a suite called '{suite}'. Add --mod, or write <mod>.{suite}.";
                    return null;
                }
                inSuites.UnionWith(matched);
            }
            return inMods.Where(inSuites.Contains).ToList();
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void List(List<Scenario> picked)
        {
            foreach (IGrouping<ModTests, Scenario> mod in picked.GroupBy(s => s.Mod))
            {
                ModTestRunnerPlugin.Log.LogInfo($"{mod.Key.Name}:");
                foreach (IGrouping<string, Scenario> suite in mod.GroupBy(s => s.Suite))
                {
                    ModTestRunnerPlugin.Log.LogInfo($"  {suite.Key} ({suite.Count()}): " + string.Join(", ", suite.Select(s => s.Name)));
                }
            }
            ModTestRunnerPlugin.Log.LogInfo($"{picked.Count} scenarios. They run by phase, grouped by stage; ones that change the run "
                + "for good run last.");
        }

        private static void LogUsage()
        {
            foreach (string line in Usage)
            {
                ModTestRunnerPlugin.Log.LogInfo(line);
            }
            ModTestRunnerPlugin.Log.LogInfo("Registered: " + (TestRegistry.Mods.Count > 0 ? string.Join(", ", TestRegistry.Mods.Select(m => m.Name)) : "none") + ".");
        }
    }
}
