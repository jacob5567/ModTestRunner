using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace ModTestRunner
{
    // Keeps a test run from writing the user profile to disk, with DebugToolkit's prevent_profile_writing. The command
    // blocks SaveSystem.Save, so the stats and unlocks the tests earn stay in memory; the next save after the run
    // writes them. Writing comes back on after the run only if it was on before, so a manual setting is kept.
    internal sealed class ProfileWriting
    {
        // DebugToolkit's private switch behind the command, true while saves go through. The command only prints
        // its state, so this is the one way to read it.
        private const string SwitchType = "DebugToolkit.Commands.Profile";
        private const string SwitchField = "canSaveProfile";

        private bool prevented;

        // Turns writing off, and returns a line for the log.
        public string Prevent(Harness harness)
        {
            bool? allowed = WritingAllowed();
            if (allowed == false) return "Profile writing was already prevented, and stays prevented after the run.";

            harness.Cmd($"{Commands.PreventProfileWriting} 1");
            prevented = true;
            return allowed == null
                ? $"Prevented profile writing until the run ends. Couldn't read whether it was prevented before " +
                  $"({SwitchType}.{SwitchField} not found), so it will be allowed again after the run."
                : "Prevented profile writing until the run ends.";
        }

        // Turns writing back on if Prevent turned it off, and returns a line for the log, or null if it did nothing.
        public string Restore(Harness harness)
        {
            if (!prevented) return null;

            harness.Cmd($"{Commands.PreventProfileWriting} 0");
            prevented = false;
            return "Allowed profile writing again.";
        }

        private static bool? WritingAllowed()
        {
            if (!Chainloader.PluginInfos.TryGetValue(Commands.DebugToolkitGUID, out BepInEx.PluginInfo info) || !info.Instance)
            {
                return null;
            }
            Type type = info.Instance.GetType().Assembly.GetType(SwitchType);
            FieldInfo field = type?.GetField(SwitchField, BindingFlags.Static | BindingFlags.NonPublic);
            return field?.FieldType == typeof(bool) ? (bool)field.GetValue(null) : (bool?)null;
        }
    }
}
