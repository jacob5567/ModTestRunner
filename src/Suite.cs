using System.Collections.Generic;
using System.Linq;

namespace ModTestRunner
{
    // A named group of a mod's scenarios, picked with mod_test --suite. The suite lists its scenarios; one scenario can
    // be in several suites (a screenshot of a panel in both "screenshots" and that panel's suite, say).
    public sealed class Suite
    {
        public readonly string Name;
        // For suites that aren't checks, or are slow, such as taking screenshots: their scenarios run only when the
        // suite is named with --suite, or a scenario with --test. A scenario also in a suite without this runs in a
        // plain mod_test too.
        public readonly bool OnlyWhenNamed;
        public readonly IReadOnlyList<Scenario> Scenarios;

        // The mod that registered it, set by TestRegistry.
        public ModTests Mod { get; internal set; }

        public Suite(string name, params Scenario[] scenarios) : this(name, false, scenarios)
        {
        }

        public Suite(string name, IEnumerable<Scenario> scenarios) : this(name, false, scenarios)
        {
        }

        public Suite(string name, bool onlyWhenNamed, params Scenario[] scenarios)
            : this(name, onlyWhenNamed, (IEnumerable<Scenario>)scenarios)
        {
        }

        public Suite(string name, bool onlyWhenNamed, IEnumerable<Scenario> scenarios)
        {
            Name = name;
            OnlyWhenNamed = onlyWhenNamed;
            Scenarios = (scenarios ?? Enumerable.Empty<Scenario>()).ToList().AsReadOnly();
        }

        // Whether it lists the scenario: by name, so a suite can list a scenario made apart from the mod's list.
        public bool Holds(Scenario scenario)
        {
            return Scenarios.Any(held => string.Equals(held.Name, scenario.Name, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
