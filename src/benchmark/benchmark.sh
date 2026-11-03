#!/bin/bash
# Measures how KSA runs on this Mac: plays a fixed test of the game in one session and writes a report, and a zip of it
# in the run's folder to share if you like. ./benchmark.sh --help lists the tests, the other commands and the options.
#
# The benchmark's program does the work (in this folder's payload/, src/benchmark/report/ in the repository): it plays
# the test with the app's benchmark and writes the report.
set -eo pipefail
# shellcheck source=../port-folder/lib/common.sh  # next to it in the port folder
source "$(dirname "$0")/lib/common.sh"

check_mac
require_port_folder
# exec: Ctrl-C at the terminal goes to the program itself, which stops the game and reports what it measured
KSA_PORT_FOLDER="${PORT_FOLDER}" DOTNET_ROOT="${SKELETON}/MacOS" exec "${SKELETON}/MacOS/dotnet" \
    "${SKELETON}/Resources/Benchmark/KsaMetalBenchmarkReport.dll" command "$@"
