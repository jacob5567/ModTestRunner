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
| `mod_test --suite <suite>` | Runs one suite. Short form: `-s`. If two mods have a suite with that name, write `<mod>.<suite>` or add `--mod`. A scenario can be in several suites. |
| `mod_test --mod <mod> --suite <suite>` | Runs one suite of one mod. |
| `mod_test --test <test>` | Runs one scenario. Short form: `-t`. If two mods have a test with that name, write `<mod>.<test>` or add `--mod`. |
| `mod_test list [flags]` | Lists what the same flags would run, without running it. |
| `mod_test help` | Shows these forms and the registered mods. |

A plain `mod_test` runs every scenario. A suite can opt out of that with `onlyWhenNamed`, for slow ones: its tests then
run only when it is named with `--suite`, or a test with `--test`, unless a test is also in a suite without the mark;
`mod_test list` names such suites.

A run goes suite by suite, in the order the mods registered and declared their suites, and each scenario runs once,
with the first suite that holds it. Each suite starts clean: its first stage loads afresh, the player is turned back
into the survivor they were when the run began, and the stage count goes back to the first stage's. Scenarios in no
suite run after the suites, and ones marked `runsLast` after everything. `mod_test list` shows that order. Screenshots go to `BepInEx/ModTestRunner/Screenshots/<run start>/`, and the summary says where.

`--mod`, `--suite` and `--test` each take a comma-separated list, as in `mod_test --mod SevenUI --suite
pickers,screenshots`, and pick what any of the names pick. Giving a flag more than once adds to its list. Together the
flags narrow each other: `--test` picks among the scenarios `--mod` and `--suite` pick. Names aren't case-sensitive.

While the tests run, the runner turns on DebugToolkit's `prevent_profile_writing`, so the stats, unlocks and
achievements the scenarios earn aren't written to disk. It turns it off again when the run ends, failed or not, unless
it was already on before the run. The changes are still in the profile in memory, so the game's next save writes them,
at the latest when the game closes. To keep them out of your main profile, leave `prevent_profile_writing 1` on until
you close the game, or test on a separate profile.

Results go to the BepInEx log (`BepInEx/LogOutput.log`), followed by a summary line and the list of failures for each mod, and a total when the run had several. Every line the runner writes about a mod's tests starts with `[<mod>.Tests]`, so `grep '\[YourMod.Tests\]'` finds a mod's whole run; a scenario's own lines can be written the same way with `h.Log(...)`.

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

    // Each suite lists its scenarios; one scenario can be in several suites. Scenarios() can list more that are in
    // no suite.
    public override IEnumerable<Suite> Suites()
    {
        yield return new Suite("pickups", new Scenario(1, "shows_on_lunar", Stages.TitanicPlains, ShowsOnLunar));
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

`new Scenario(phase, name, scene, run, runsLast: false, takesDamage: false)`

- **phase**: within a suite, scenarios run in ascending phase order. Put scenarios that change a stage for good
  (starting its teleporter, killing its boss) in a later phase than the ones that need it untouched.
- **name**: what `--test` picks, and what suites list the scenario by. Unique among the mod's scenarios; its full name
  is `<mod>.<name>`, as in the log and screenshot file names.
- **scene**: the stage to run in (`Stages.*`, or any `SceneDef.cachedName`). Each stage is loaded once for all the
  scenarios in a phase that use it. Pass null to run wherever the player is.
- **runsLast**: for scenarios that change the run itself, such as earning a one-time unlock. They run after every
  suite, together.
- **takesDamage**: for scenarios that need the player to take damage. Every other scenario runs with the player in god
  mode, kept on through new stages and survivors; the player gets back whatever they had when the run ends.

### Suite

`new Suite(name, onlyWhenNamed: false, scenarios...)`, returned from `ModTests.Suites()`

- **name**: what `--suite` picks.
- **onlyWhenNamed**: for suites that aren't checks, or are slow, such as taking screenshots. Their scenarios run only
  when the suite is named with `--suite` or a scenario with `--test`, not in a plain `mod_test` or `mod_test --mod`,
  unless a scenario is also in a suite without it. `mod_test list` names them.
- **scenarios**: the suite's scenarios, as an array or a list. A scenario can be in any number of suites: list it in
  each, built by the same method or not, since suites hold scenarios by name. The runner runs each once.

To give your `Precondition` more to go on, subclass `Scenario`. For example, KarmaWarnings' `KarmaScenario` lists the
triggers a scenario needs unearned.

### What `Harness` provides

| Area | Members |
|---|---|
| Waiting and checking | `Until`, `Holds`, `Wait`, `Fail`, `Skip`, `Diagnosis` |
| Commands and spawning | `Cmd` (with `Commands.*`), `Spawned<T>`, `SpawnPrefab<T>`, `SpawnPickup`, `ItemOfTier`, `FindBody` |
| Moving and aiming | `StandBefore`, `StandNear`, `Teleport`, `AimAt`, `AimAway`, `Interact` |
| State | `Body`, `LocalBody`, `SetHealthFraction`, `GoTo` (another stage mid-scenario) |
| Screenshots | `Screenshot(label)`: saves the screen as `BepInEx/ModTestRunner/Screenshots/<run start>/<mod>.<scenario>.<label>.png`, with the console hidden for that frame |
| Logging | `Log(message)`: writes a line to the BepInEx log tagged `[<mod>.Tests]`, as the runner's own lines are |
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
