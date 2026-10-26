using System.Collections;
using System.Collections.Generic;

namespace ModTestRunner
{
    // One mod's tests. A test plugin subclasses this and registers an instance with TestRegistry.Register in its Awake.
    public abstract class ModTests
    {
        // The name mod_test --mod picks it by, and the first part of each scenario's full name. No spaces.
        public abstract string Name { get; }

        // Every scenario, in file order. The runner orders them (see Scenario).
        public abstract IEnumerable<Scenario> Scenarios();

        // Why the scenario can't run now (the mod is turned off, what it tests is already done, ...), or null. Called
        // before each scenario, once a run is in progress.
        public virtual string Precondition(Scenario scenario)
        {
            return null;
        }

        // Whether the mod has finished setting itself up after a stage loads, for example its HUD controller exists.
        // Loading a stage waits for it.
        public virtual bool Ready()
        {
            return true;
        }

        // Puts back what the mod's scenarios change, after the runner's own cleanup (spawned objects, inventory,
        // monsters, and anything set with Harness.SetConfig or Harness.OnCleanup).
        public virtual IEnumerator Cleanup(Harness h)
        {
            yield break;
        }

        // What the mod saw when a check failed, for the failure message, or null.
        public virtual string Diagnose()
        {
            return null;
        }
    }
}
