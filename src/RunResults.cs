using System;
using System.Collections.Generic;

namespace ModTestRunner
{
    // A test run's outcome, as the automated run writes it to results.json for run-tests.sh (JsonUtility, so public
    // fields).
    [Serializable]
    internal sealed class RunResults
    {
        public const string StatusPassed = "passed";
        public const string StatusFailed = "failed";
        // The run stopped before its last scenario, on an exception in the runner.
        public const string StatusAborted = "aborted";
        // The automated run never got to run the tests, or mod_test refused them.
        public const string StatusError = "error";
        public const string StatusTimedOut = "timed out";

        public string Status;
        // The screen size the tests ran at, "<width>x<height>".
        public string Resolution;
        // Why, for an error or a timeout.
        public string Message;
        public List<ModResult> Mods = new List<ModResult>();

        [Serializable]
        internal sealed class ModResult
        {
            public string Name;
            public int Passed;
            public int Failed;
            public int Skipped;
            public List<string> Failures = new List<string>();
        }
    }
}
