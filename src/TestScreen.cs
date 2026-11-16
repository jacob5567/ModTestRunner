using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Path = System.IO.Path;

namespace ModTestRunner
{
    // The screen size an automated run tests at, and the player's own setting, put back before the game quits. The
    // game's resolution setting reads back the live screen size (SettingsConVars.ResolutionConVar) and is saved when the
    // game quits, so the test size, or the 1024x768 fallback a launch can start at, would otherwise replace the
    // player's setting. Their setting is read from the config file before the game can rewrite it.
    internal sealed class TestScreen
    {
        public const int DefaultWidth = 2560;
        public const int DefaultHeight = 1440;

        // How long the screen gets to reach a new size before the run gives up on it.
        private const float ResizeSeconds = 15f;
        private const string ResolutionSetting = "resolution";
        // The refresh rate part of a resolution, when the player's setting has none to keep: the display's default.
        private const string DefaultRefresh = "0";

        public readonly int Width;
        public readonly int Height;
        // The player's setting as the config file has it, "<width>x<height>x<refresh>", or null if it couldn't be read.
        private readonly string playerSetting;

        public TestScreen(int width, int height)
        {
            Width = width;
            Height = height;
            playerSetting = ReadPlayerSetting();
        }

        public string Size => $"{Width}x{Height}";

        // "2560x1440" into its numbers.
        public static bool TryParse(string value, out int width, out int height)
        {
            width = height = 0;
            string[] parts = value?.Split('x') ?? Array.Empty<string>();
            return parts.Length >= 2 &&
                   int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
                   int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) &&
                   width > 0 && height > 0;
        }

        // Sets the test size and waits for the screen to reach it, or tells fail why it didn't.
        public IEnumerator Apply(Action<string> fail)
        {
            string refresh = playerSetting?.Split('x').ElementAtOrDefault(2) ?? DefaultRefresh;
            AutoRun.Cmd($"{ResolutionSetting} {Width}x{Height}x{refresh}");
            yield return WaitForSize(Width, Height);
            if (!Is(Width, Height))
            {
                fail($"The screen stayed {Screen.width}x{Screen.height} instead of the {Size} asked for.");
            }
        }

        // Puts the player's setting back and waits for the screen to reach it, so the game saves it when it quits.
        public IEnumerator Restore()
        {
            if (playerSetting == null || !TryParse(playerSetting, out int width, out int height))
            {
                ModTestRunnerPlugin.Log.LogWarning("Couldn't read the player's resolution from config.cfg, so the game " +
                                                   $"will save the screen's size, {Screen.width}x{Screen.height}, as theirs.");
                yield break;
            }
            AutoRun.Cmd($"{ResolutionSetting} {playerSetting}");
            yield return WaitForSize(width, height);
            if (!Is(width, height))
            {
                ModTestRunnerPlugin.Log.LogWarning($"The screen didn't go back to {width}x{height}, so the game will save " +
                                                   $"{Screen.width}x{Screen.height} as the player's resolution.");
            }
        }

        private static IEnumerator WaitForSize(int width, int height)
        {
            float until = Time.realtimeSinceStartup + ResizeSeconds;
            while (!Is(width, height) && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
        }

        private static bool Is(int width, int height) => Screen.width == width && Screen.height == height;

        // The "resolution <width>x<height>x<refresh>;" line of the game's Config/config.cfg, which the game runs at startup.
        private static string ReadPlayerSetting()
        {
            string path = Path.Combine(Application.dataPath, "Config", "config.cfg");
            try
            {
                string line = File.ReadAllLines(path).Select(each => each.Trim())
                    .FirstOrDefault(each => each.StartsWith(ResolutionSetting + " ", StringComparison.Ordinal));
                string value = line?.Substring(ResolutionSetting.Length).Trim().TrimEnd(';').Trim('"');
                return TryParse(value, out _, out _) ? value : null;
            }
            catch (Exception exception)
            {
                ModTestRunnerPlugin.Log.LogWarning($"Couldn't read {path}: {exception.Message}");
                return null;
            }
        }
    }
}
