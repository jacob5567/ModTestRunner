#!/usr/bin/env bash
# Steam launch wrapper for Risk of Rain 2. An automated test run (run-tests.sh adds --mod-test=...) goes inside a
# headless gamescope: a virtual screen of the test size, so the run works with the monitor off or asleep and never
# takes over the desktop. Any other launch runs the game as usual. In the game's Steam launch options, it goes before
# %command%, ahead of what r2modman needs:
#
#   /home/jacob/source/RoR2Mods/ModTestRunner/steam-launch.sh %command% --doorstop-enabled true ...
#
# run-tests.sh --visible adds --mod-test-visible, which skips gamescope so the run can be watched.
set -euo pipefail

automated=false
visible=false
resolution=2560x1440
for arg in "$@"; do
    case "$arg" in
        --mod-test=*) automated=true ;;
        --mod-test-visible) visible=true ;;
        --mod-test-resolution=*) resolution=${arg#*=} ;;
    esac
done

if $automated && ! $visible && command -v gamescope > /dev/null; then
    width=${resolution%%x*}
    height=${resolution#*x}
    # -W/-H are the virtual output, -w/-h the size the game renders at: the same, so nothing is scaled.
    exec gamescope --backend headless -W "$width" -H "$height" -w "$width" -h "$height" -- "$@"
fi
exec "$@"
