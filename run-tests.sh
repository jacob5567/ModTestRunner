#!/usr/bin/env bash
# Runs mod tests with no one at the keyboard: launches the game through Steam with the r2modman profile, lets
# ModTestRunner's automated run start a single-player run, run mod_test and quit, then prints the results. Arguments
# are mod_test's; with none, every registered mod's scenarios run.
#
#   ./run-tests.sh --mod SevenUI
#   ./run-tests.sh --timeout 1800 --mod KarmaWarnings --suite basics
#   ./run-tests.sh --resolution 1920x1080 --mod SevenUI
#
# --timeout <seconds> (default 900) and --resolution <width>x<height> (default 2560x1440) come first, in either order.
#
# Exit status: 0 all passed, 1 some failed, 2 the run didn't finish (error, timeout, crash, or the game already running).
# Environment: R2_PROFILE (r2modman profile name, default Default), STEAM (path to steam.sh).
set -euo pipefail

app_id=632360
# The game's process name: "Risk of Rain 2.exe", cut to Linux's 15 characters. Matching it exactly can't match a
# shell command that merely mentions the game.
game_process="Risk of Rain 2."
profile_name=${R2_PROFILE:-Default}
profile_dir="$HOME/.config/r2modmanPlus-local/RiskOfRain2/profiles/$profile_name"
steam=${STEAM:-$HOME/.local/share/Steam/steam.sh}
results="$profile_dir/BepInEx/ModTestRunner/results.json"
game_dir="$HOME/.local/share/Steam/steamapps/common/Risk of Rain 2"
# The game's settings: the copy in the game folder and the Steam Cloud copy. The game saves the screen's size in them
# as the player's resolution when it quits.
game_configs=("$game_dir/Risk of Rain 2_Data/Config/config.cfg")
for cloud_config in "$HOME"/.local/share/Steam/userdata/*/632360/remote/UserProfiles/config.cfg; do
    [ -f "$cloud_config" ] && game_configs+=("$cloud_config")
done
log="$profile_dir/BepInEx/LogOutput.log"

# The game's own limit for the whole run; this script waits longer, for the launch, before it gives up on the game.
timeout=900
launch_grace=180
quit_grace=60
poll=2

resolution=2560x1440
while [ $# -ge 2 ]; do
    case "$1" in
        --timeout) timeout=$2 ;;
        --resolution) resolution=$2 ;;
        *) break ;;
    esac
    shift 2
done
test_args="$*"

game_running() { pgrep -x "$game_process" > /dev/null; }

if [ ! -d "$profile_dir/BepInEx" ]; then
    echo "No r2modman profile at $profile_dir" >&2
    exit 2
fi
if game_running; then
    echo "The game is already running; close it first." >&2
    exit 2
fi
# With no monitor connected (one that is off or asleep reports disconnected), the game starts at a 1024x768 fallback
# and can't change size, so layout checks can't pass.
if ! grep -qx connected /sys/class/drm/card*-*/status 2>/dev/null; then
    echo "No monitor is connected (or it's off or asleep): the game can't run at $resolution. Wake the monitor first." >&2
    exit 2
fi
if ! pgrep -x steam > /dev/null; then
    echo "Steam isn't running. Starting it with the game; it may wait for you to sign in." >&2
fi

rm -f "$results"
# The player's resolution, put back after the game quits if the run changed it (the runner tries too, but can't when the
# screen won't resize).
player_resolution=$(grep -m1 '^resolution ' "${game_configs[0]}" 2>/dev/null || true)
restore_resolution() {
    [ -n "$player_resolution" ] || return 0
    for config in "${game_configs[@]}"; do
        if ! grep -qxF "$player_resolution" "$config"; then
            sed -i "s/^resolution .*/$player_resolution/" "$config"
            echo "Put the player's ${player_resolution%;} back in $config"
        fi
    done
}
echo "Launching the game: mod_test ${test_args:-(everything)} (at $resolution, timeout ${timeout}s)"
# The same launch as r2modman's, plus the automated run's arguments, each one argument with its value after "=".
# steam.sh hands the launch to a running Steam and exits, or runs Steam itself, so it goes in the background.
"$steam" -applaunch "$app_id" \
    --doorstop-enabled true \
    --doorstop-target-assembly "Z:$profile_dir/BepInEx/core/BepInEx.Preloader.dll" \
    --r2profile "$profile_name" \
    "--mod-test-timeout=$timeout" \
    "--mod-test-resolution=$resolution" \
    "--mod-test=$test_args" > /dev/null 2>&1 &
disown

started=$SECONDS
seen=false
while [ ! -f "$results" ]; do
    elapsed=$((SECONDS - started))
    if game_running; then
        seen=true
    elif $seen; then
        echo "The game closed without writing results. See $log" >&2
        restore_resolution
        exit 2
    elif [ "$elapsed" -gt "$launch_grace" ]; then
        echo "The game didn't start within ${launch_grace}s." >&2
        exit 2
    fi
    if [ "$elapsed" -gt $((timeout + launch_grace)) ]; then
        echo "No results after ${elapsed}s; killing the game. See $log" >&2
        pkill -x "$game_process" || true
        exit 2
    fi
    sleep "$poll"
done

# The game quits by itself after writing the results.
for _ in $(seq $((quit_grace / poll))); do
    game_running || break
    sleep "$poll"
done
if game_running; then
    echo "The game didn't quit within ${quit_grace}s of finishing; killing it." >&2
    pkill -x "$game_process" || true
fi

if ! game_running; then
    restore_resolution
fi
echo "Took $((SECONDS - started))s."
python3 -I - "$results" "$log" <<'EOF'
import json, sys
results = json.load(open(sys.argv[1]))
status = results.get("Status", "")
print(f"Result: {status} at {results.get('Resolution', '?')}" + (f": {results['Message']}" if results.get("Message") else ""))
for mod in results.get("Mods", []):
    print(f"  {mod['Name']}: {mod['Passed']} passed, {mod['Failed']} failed, {mod['Skipped']} skipped")
    # Only each failure's first line, cut short: the diagnosis after it can run to pages, and it is in results.json.
    for failure in mod.get("Failures", []):
        line = failure.splitlines()[0] if failure else ""
        print(f"    FAIL {line[:300]}" + (" ..." if len(line) > 300 else ""))
print(f"Results: {sys.argv[1]}")
print(f"Log: {sys.argv[2]}")
sys.exit({"passed": 0, "failed": 1}.get(status, 2))
EOF
