# ModTestRunner

Runs in-game tests for Risk of Rain 2 mods. Each mod's tests live in a small test plugin of its own, which registers
its scenarios with the runner. The runner loads stages, sets the game up with DebugToolkit, runs each scenario as a
coroutine, cleans up after it, and logs PASS, FAIL or SKIP for each one.

It's a development tool, so don't ship it or your test plugin to players.

## Requirements

- [DebugToolkit](https://thunderstore.io/package/IHarbHD/DebugToolkit/). The scenarios set the game up with its commands.
- Running the tests as the host, or in single player. The runner spawns objects and moves the player on the server.

## Running tests

Start a run, then open the console (Ctrl+Alt+`) and use `mod_test`:

| Command | What it does |
|---|---|
| `mod_test` | Runs every registered mod's scenarios. |
| `mod_test --mod <mod>` | Runs one mod's scenarios. Short form: `-m`. |
| `mod_test --suite <suite>` | Runs one suite. Short form: `-s`. If two mods have a suite with that name, write `<mod>.<suite>` or add `--mod`. |
| `mod_test --mod <mod> --suite <suite>` | Runs one suite of one mod. |
| `mod_test list [flags]` | Lists what the same flags would run, without running it. |
| `mod_test help` | Shows these forms and the registered mods. |

`--mod` and `--suite` can each be given more than once. Names aren't case-sensitive.

Results go to the BepInEx log (`BepInEx/LogOutput.log`), followed by a summary line and the list of failures.

## Writing tests for a mod

For a complete example, see [KarmaWarnings' tests](https://github.com/jacob5567/KarmaWarnings/tree/main/tests).

1. Put the tests in a `tests/` folder in the mod's repo. It holds only `.cs` files: no project file, and no build
   output. ModTestRunner builds them (see [Building a mod's tests](#building-a-mods-tests)). To test the mod's
   internals, add `[assembly: InternalsVisibleTo("<YourMod>.Tests")]` to the mod. If the mod's project is at the repo
   root, also exclude the tests from it, so they don't end up in the mod's DLL:
   `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>`.
2. Subclass `ModTests` and register it in the plugin's `Awake`:

```csharp
[BepInPlugin("you.YourMod.Tests", "YourMod.Tests", "0.0.1")]
[BepInDependency(YourModPlugin.PluginGUID)]
[BepInDependency(ModTestRunnerPlugin.PluginGUID)]
public class YourModTestsPlugin : BaseUnityPlugin
{
    private void Awake() => TestRegistry.Register(new YourModTests());
}

internal sealed class YourModTests : ModTests
{
    public override string Name => "YourMod";

    public override IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario(1, "pickups", "shows_on_lunar", Stages.TitanicPlains, ShowsOnLunar);
    }

    // Optional overrides:
    //   Precondition(scenario)  why a scenario can't run now (the mod is turned off, ...), or null
    //   Ready()                 whether the mod has attached after a stage loads; stage loads wait for it
    //   Cleanup(h)              puts back what your scenarios change, after the runner's own cleanup
    //   Diagnose()              what your mod saw, added to every failure message

    private static IEnumerator ShowsOnLunar(Harness h)
    {
        GenericPickupController pickup = h.SpawnPickup(Harness.ItemOfTier(ItemTier.Lunar));
        yield return h.StandBefore(pickup.gameObject);
        yield return h.Until(() => YourModHud.Showing, "the hint to show");
    }
}
```

3. Put checks you use often in extension methods on `Harness`, so scenarios call them like the built-in helpers:

```csharp
public static IEnumerator ExpectHint(this Harness h, string text)
{
    yield return h.Until(() => YourModHud.Text == text, () => $"the hint \"{text}\" (showing \"{YourModHud.Text}\") {h.Diagnosis()}");
}
```

A scenario is a coroutine. Yield another coroutine to run it nested, yield `null` to wait a frame, and throw
`h.Fail(...)` or `h.Skip(...)` to end the scenario. Any other exception also fails the scenario, and its stack trace
is logged.

### Scenario

`new Scenario(phase, suite, name, scene, run, runsLast: false)`

- **phase**: scenarios run in ascending phase order. Put scenarios that change a stage for good (starting its teleporter,
  killing its boss) in a later phase than the ones that need it untouched.
- **suite**: the group `--suite` picks.
- **scene**: the stage to run in (`Stages.*`, or any `SceneDef.cachedName`). Each stage is loaded once for all the
  scenarios in a phase that use it. Pass null to run wherever the player is.
- **runsLast**: for scenarios that change the run itself, such as earning a one-time unlock. They run after everything
  else.

To give your `Precondition` more to go on, subclass `Scenario`. For example, KarmaWarnings' `KarmaScenario` lists the
triggers a scenario needs unearned.

### What `Harness` provides

| Area | Members |
|---|---|
| Waiting and checking | `Until`, `Holds`, `Wait`, `Fail`, `Skip`, `Diagnosis` |
| Commands and spawning | `Cmd` (with `Commands.*`), `Spawned<T>`, `SpawnPrefab<T>`, `SpawnPickup`, `ItemOfTier`, `FindBody` |
| Moving and aiming | `StandBefore`, `StandNear`, `Teleport`, `AimAt`, `AimAway`, `Interact` |
| State | `Body`, `LocalBody`, `SetHealthFraction`, `GoTo` (another stage mid-scenario) |
| Cleanup | `SetConfig` (changes a config entry for one scenario), `OnCleanup` (any other restore step) |

After every scenario, the runner destroys what `Spawned`, `SpawnPrefab` and `SpawnPickup` made. It then undoes the
`SetConfig` and `OnCleanup` changes, clears the inventory, kills all monsters, and runs your `Cleanup`.

## Building

Needs the .NET 8 SDK. `dotnet build -c Release` builds the plugin and copies it into the r2modman profile set in
`Directory.Build.props` (`-p:CopyToProfile=false` turns the copy off).

### Building a mod's tests

```bash
./build-tests.sh <mod repo>        # for example ./build-tests.sh ../KarmaWarnings
```

This compiles every `.cs` file under `<mod repo>/tests/` into `<ModName>.Tests.dll`, using
[build/ModTests.csproj](build/ModTests.csproj). The build output stays in this repo, under
`obj/tests/<ModName>/` and `bin/tests/<ModName>/`. The build also builds the mod and the runner, and copies all three
into the profile. Arguments after the repo go to `dotnet build`, for example `-p:CopyToProfile=false`.

- `<ModName>` is the repo folder's name, and the repo must have a `<ModName>.csproj` at its root. If the folder is
  named differently, pass `-p:ModName=<name>`.
- The tests are compiled against the game assemblies, BepInEx, the mod and the runner, and against Risk Of Options
  when the profile has it.
- With no project file in `tests/`, an editor that opens the mod's repo doesn't analyze those files, so compile
  errors only show up when you build.

## License

[GPL-3.0](LICENSE)
