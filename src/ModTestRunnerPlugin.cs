using BepInEx;
using BepInEx.Logging;
using RoR2;

// Lets the [ConCommand] method below be found: the game only scans assemblies that opt in.
[assembly: HG.Reflection.SearchableAttribute.OptIn]

namespace ModTestRunner
{
    // Runs the in-game tests that mods' test plugins register with TestRegistry. Dev-only: it is never packaged or published.
    //
    // DebugToolkit is a soft dependency: its commands (next_stage, give_item, kill_all, ...) are how scenarios set the
    // game up, and the runner reports a clear error when they are missing.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Commands.DebugToolkitGUID, BepInDependency.DependencyFlags.SoftDependency)]
    public class ModTestRunnerPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "sevenstack.ModTestRunner";
        public const string PluginName = "ModTestRunner";
        public const string PluginVersion = "0.0.1";

        internal static ManualLogSource Log;
        // Scenarios are coroutines, started on this instance.
        internal static ModTestRunnerPlugin Instance;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Logger.LogInfo("ModTestRunner loaded. Start a run, then use mod_test, or mod_test help.");
        }

        private void OnDestroy()
        {
            Instance = null;
        }

        [ConCommand(commandName = "mod_test", flags = ConVarFlags.None, helpText = TestCommand.HelpText)]
        private static void CCModTest(ConCommandArgs args)
        {
            TestCommand.Run(args);
        }
    }
}
