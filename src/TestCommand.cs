using System;
using System.Collections.Generic;
using System.Linq;
using RoR2;

namespace ModTestRunner
{
    // mod_test: picks scenarios by mod, suite and test, then lists or runs them. Every flag can be given more than once.
    internal static class TestCommand
    {
        public const string HelpText = "Runs registered in-game mod tests. mod_test [list] [--mod <mod>] [--suite <suite>] "
            + "[--test <test>]. "
            + "mod_test help for more.";

        private static readonly string[] Usage =
        {
            "mod_test                          runs every registered mod's scenarios",
            "mod_test --mod <mod>              runs one mod's scenarios (-m)",
            "mod_test --suite <suite>          runs one suite (-s); write <mod>.<suite> when two mods have a suite of that name",
            "                                  (a scenario can be in several suites)",
            "mod_test --mod <mod> --suite <s>  runs that mod's suite",
            "mod_test --test <test>            runs one scenario (-t); write <mod>.<test> when two mods have a test of that name",
            "mod_test list [...]               lists what the same flags would run, instead of running it",
            "Every scenario runs in a plain mod_test, except those only in suites marked to run only when named with --suite "
                + "(or a test with --test).",
        };

        public static void Run(ConCommandArgs args)
        {
            bool list = false;
            List<string> mods = new List<string>();
            List<string> suites = new List<string>();
            List<string> tests = new List<string>();
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
                    case "--test":
                    case "-t":
                        if (i + 1 >= args.Count)
                        {
                            ModTestRunnerPlugin.Log.LogWarning($"{arg} needs a name. See mod_test help.");
                            return;
                        }
                        List<string> names = flag == "--mod" || flag == "-m" ? mods : flag == "--suite" || flag == "-s" ? suites : tests;
                        names.Add(args[++i]);
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

            List<Scenario> picked = Pick(all, mods, suites, tests, out string error);
            if (error != null)
            {
                // Tagged with the mods named, when they have tests.
                RunLog.Warning(string.Join(" ", mods.Select(TestRegistry.Find).Where(found => found != null).Select(RunLog.Tag)), error);
                return;
            }

            if (list)
            {
                List(RunPlan.Split(picked, suites), picked.Count);
            }
            else
            {
                Harness.Start(RunPlan.Split(picked, suites));
            }
        }

        // The scenarios of the named mods, suites and tests, still in run order, or null and why. Naming a suite or a
        // test runs it even if its suite runs only when named.
        private static List<Scenario> Pick(List<Scenario> all, List<string> mods, List<string> suites, List<string> tests,
            out string error)
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
            List<Scenario> picked = mods.Count == 0 ? all : all.Where(s => mods.Any(mod => Same(s.Mod.Name, mod))).ToList();
            if (suites.Count > 0)
            {
                picked = PickSuites(picked, mods, suites, out error);
                if (picked == null) return null;
            }
            if (tests.Count > 0)
            {
                return PickTests(picked, mods, suites, tests, out error);
            }
            return suites.Count > 0 ? picked : picked.Where(s => !s.OnlyWhenNamed).ToList();
        }

        private static List<Scenario> PickSuites(List<Scenario> inMods, List<string> mods, List<string> suites, out string error)
        {
            error = null;
            HashSet<Scenario> inSuites = new HashSet<Scenario>();
            foreach (string suite in suites)
            {
                List<Scenario> matched = inMods.Where(s => s.Suites.Any(own => Same(own.Name, suite) ||
                                                                               Same($"{s.Mod.Name}.{own.Name}", suite))).ToList();
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

        // A test is named by its own name, or <mod>.<name>, among the scenarios the mods and suites picked.
        private static List<Scenario> PickTests(List<Scenario> candidates, List<string> mods, List<string> suites,
            List<string> tests, out string error)
        {
            error = null;
            HashSet<Scenario> named = new HashSet<Scenario>();
            foreach (string test in tests)
            {
                List<Scenario> matched = candidates.Where(s => Same(s.Name, test) || Same(s.FullName, test)).ToList();
                if (matched.Count == 0)
                {
                    List<string> scope = mods.Concat(suites).ToList();
                    error = $"No test called '{test}'" + (scope.Count > 0 ? $" in {string.Join(", ", scope)}" : "") +
                            ". Use mod_test list.";
                    return null;
                }
                List<string> owners = matched.Select(s => s.Mod.Name).Distinct().ToList();
                if (owners.Count > 1)
                {
                    error = $"{string.Join(" and ", owners)} each have a test called '{test}'. Add --mod, or write <mod>.{test}.";
                    return null;
                }
                named.UnionWith(matched);
            }
            return candidates.Where(named.Contains).ToList();
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // What the run would do, in order: each part, from a clean start, and its scenarios.
        private static void List(List<RunPart> plan, int count)
        {
            foreach (RunPart part in plan)
            {
                RunLog.Info(RunLog.Tags(part.Scenarios), $"  {part.Name} ({part.Scenarios.Count}): " +
                                                string.Join(", ", part.Scenarios.Select(s => s.Name)));
            }
            RunLog.Info(RunLog.Tags(plan.SelectMany(part => part.Scenarios)),
                $"{count} scenarios, run suite by suite in this order, each suite from the first stage "
                + "and the survivor the run began as. A scenario in several suites runs once, with the first; within a suite "
                + "they run by phase, grouped by stage; ones that change the run for good run last.");
            List<Scenario> picked = plan.SelectMany(part => part.Scenarios).ToList();
            List<Scenario> left = TestRegistry.Scenarios().Where(s => s.OnlyWhenNamed && !picked.Any(p => p.FullName == s.FullName))
                .ToList();
            List<string> namedOnly = left.SelectMany(s => s.Suites.Select(own => $"{s.Mod.Name}.{own.Name}"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (namedOnly.Count > 0)
            {
                RunLog.Info(RunLog.Tags(left), "Run only when named with --suite, or their tests with --test: "
                    + string.Join(", ", namedOnly) + ".");
            }
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
