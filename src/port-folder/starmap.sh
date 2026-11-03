#!/bin/bash
# Adds StarMap, the KSA mod loader (https://github.com/StarMapLoader/StarMap), to Kitten Space Agency.app, or takes it
# out again (--remove). ./starmap.sh --help lists the options; the port's program does the work (lib/porter/).
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

check_mac
require_port_folder
run_porter starmap "$@"
