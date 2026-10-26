#!/usr/bin/env bash
# Builds a mod's in-game tests (the .cs files in <mod repo>/tests/) into <ModName>.Tests.dll, and copies it, the mod and
# the runner into the r2modman profile. Extra arguments go to dotnet build, for example -p:CopyToProfile=false.
#
#   ./build-tests.sh ../KarmaWarnings
set -euo pipefail

if [ $# -lt 1 ]; then
    echo "usage: $0 <mod repo> [dotnet build options]" >&2
    exit 1
fi
mod_dir=$(realpath "$1")
shift

cd "$(dirname "$0")"
dotnet build build/ModTests.csproj -c Release -p:ModDir="$mod_dir" "$@"
