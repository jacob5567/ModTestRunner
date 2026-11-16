using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using RoR2;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ModTestRunner
{
    // Runs the tests with no one at the keyboard, when the game is launched with --mod-test=<mod_test arguments>
    // (run-tests.sh does this): from the title screen into a single-player run with the game's own console commands,
    // then mod_test, then results.json, then quit. Without the argument it does nothing.
    internal sealed class AutoRun
    {
        // Each a single argument with its value after "=": an empty one can't be lost, and Steam adds the game's own launch
        // options after them, which a value in the next argument would run into.
        public const string ArgPrefix = "--mod-test=";
        public const string TimeoutArgPrefix = "--mod-test-timeout=";
        // <width>x<height>, default TestScreen's.
        public const string ResolutionArgPrefix = "--mod-test-resolution=";
        public const string ResultsFileName = "results.json";

        // Covers the whole run, from launch to the last scenario; run-tests.sh waits a little longer before it kills the game.
        private const float DefaultTimeoutSeconds = 1800f;
        // After the player's body first appears, so the stage and the HUD finish setting up before the first scenario.
        private const float SettleSeconds = 3f;
        // After the lobby appears, so the local player's survivor choice is in before the run starts.
        private const float LobbySettleSeconds = 1f;
        private const string TitleScene = "title";
        private const string TimedOutPrefix = "Timed out";

        private readonly List<string> testArgs;
        private readonly TestScreen screen;
        private readonly float deadline;
        private string failure;
        private RunResults results;

        private AutoRun(List<string> testArgs, TestScreen screen, float timeoutSeconds)
        {
            this.testArgs = testArgs;
            this.screen = screen;
            deadline = Time.realtimeSinceStartup + timeoutSeconds;
        }

        public static string ResultsPath => Path.Combine(Paths.BepInExRootPath, nameof(ModTestRunner), ResultsFileName);

        // Starts an automated run if the command line asks for one.
        public static void StartIfRequested(MonoBehaviour host)
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            string value = Value(commandLine, ArgPrefix);
            if (value == null) return;

            // Split the way the console would split it.
            List<string> testArgs = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();
            float timeout = float.TryParse(Value(commandLine, TimeoutArgPrefix), NumberStyles.Float, CultureInfo.InvariantCulture,
                out float parsed) && parsed > 0f ? parsed : DefaultTimeoutSeconds;

            if (!TestScreen.TryParse(Value(commandLine, ResolutionArgPrefix), out int width, out int height))
            {
                width = TestScreen.DefaultWidth;
                height = TestScreen.DefaultHeight;
            }
            // Made now, while the plugins load: it reads the player's resolution before the game can save over it.
            TestScreen screen = new TestScreen(width, height);

            ModTestRunnerPlugin.Log.LogInfo($"Automated test run: mod_test {string.Join(" ", testArgs)} " +
                                            $"(at {screen.Size}, timeout {timeout:0}s, results in {ResultsPath}).");
            host.StartCoroutine(new AutoRun(testArgs, screen, timeout).Steps());
        }

        // What follows the prefix in the first argument that starts with it, or null if none does.
        private static string Value(string[] commandLine, string prefix)
        {
            string arg = commandLine.FirstOrDefault(each => each.StartsWith(prefix, StringComparison.Ordinal));
            return arg?.Substring(prefix.Length);
        }

        private IEnumerator Steps()
        {
            // A results file from an earlier run must not be mistaken for this one's.
            TryDelete(ResultsPath);

            yield return Until(() => SceneManager.GetActiveScene().name == TitleScene && RoR2.Console.instance &&
                                     LocalUserManager.isAnyUserSignedIn,
                "the title screen with a signed-in profile (the profile in config.cfg's user_profile_main)");
            if (failure == null && !Chainloader.PluginInfos.ContainsKey(Commands.DebugToolkitGUID))
            {
                failure = "DebugToolkit is not installed: the scenarios set the game up with its commands.";
            }
            if (failure == null)
            {
                // Layout checks depend on the screen's size, and a launch can start at a fallback size.
                yield return screen.Apply(reason => failure = reason);
            }

            if (failure == null)
            {
                // Cheats turn off stats, achievements and unlocks until the game restarts, and stage1_pod needs them.
                // Profile writing stays prevented until the game quits: the runner leaves it as it found it.
                Cmd("cheats 1");
                Cmd($"{Commands.PreventProfileWriting} 1");
                Cmd("stage1_pod 0");
                Cmd("host 0");
                yield return Until(() => PreGameController.instance && NetworkUser.readOnlyLocalPlayersList.Count > 0,
                    "the lobby");
            }
            if (failure == null)
            {
                yield return new WaitForSecondsRealtime(LobbySettleSeconds);
                Cmd("pregame_start_run");
                yield return Until(() => RoR2.Run.instance && Stage.instance && Harness.LocalBody, "the first stage and the player's body");
            }
            if (failure == null)
            {
                yield return new WaitForSecondsRealtime(SettleSeconds);
                Harness.Finished += OnFinished;
                TestCommand.Run(testArgs);
                if (!Harness.IsRunning && results == null)
                {
                    failure = $"mod_test {string.Join(" ", testArgs)} didn't start a run; the BepInEx log says why.";
                }
            }
            if (failure == null)
            {
                yield return Until(() => results != null, "the tests to finish");
            }
            Harness.Finished -= OnFinished;

            if (results == null)
            {
                results = new RunResults
                {
                    Status = failure.StartsWith(TimedOutPrefix) ? RunResults.StatusTimedOut : RunResults.StatusError,
                    Message = failure,
                };
            }
            results.Resolution = screen.Size;
            Write(results);
            ModTestRunnerPlugin.Log.LogInfo($"Automated test run: {results.Status}." +
                                            (string.IsNullOrEmpty(results.Message) ? "" : $" {results.Message}") + " Quitting.");
            yield return screen.Restore();
            Cmd("quit");
        }

        private void OnFinished(RunResults finished)
        {
            results = finished;
        }

        // Waits until the condition holds, or records a timeout naming what it waited for.
        private IEnumerator Until(Func<bool> condition, string what)
        {
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    failure = $"{TimedOutPrefix} waiting for {what}.";
                    yield break;
                }
                yield return null;
            }
        }

        // Commands go in with no sender, as from the game's own console: the runner is the host, so the server-only
        // ones (pregame_start_run) are allowed.
        internal static void Cmd(string command)
        {
            RoR2.Console.instance.SubmitCmd((NetworkUser)null, command);
        }

        private static void Write(RunResults results)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath));
                // Written whole and then moved, so run-tests.sh never reads a half-written file.
                string partial = ResultsPath + ".tmp";
                File.WriteAllText(partial, JsonUtility.ToJson(results, true));
                File.Move(partial, ResultsPath);
            }
            catch (Exception exception)
            {
                ModTestRunnerPlugin.Log.LogError($"Couldn't write {ResultsPath}: {exception.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                ModTestRunnerPlugin.Log.LogWarning($"Couldn't delete the old {path}: {exception.Message}");
            }
        }
    }
}
