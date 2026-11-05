#!/bin/bash
# Turns the Linux release of Kitten Space Agency into a Mac app, ~/Applications/Kitten Space Agency.app, or updates
# the one you have. Saves, settings and StarMap stay. ./port.sh --help lists the options.
#
# The port's program does the work (lib/port-cli/, src/player/port-cli/ in the repository). After --update it has put
# a newer release of this folder in .update/: this script then replaces the folder's contents with it and runs the new
# port.sh, without --update.
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

check_mac
require_port_folder
status=0
run_port_cli port "$@" || status=$?
[ "${status}" = 75 ] || exit "${status}"

NEW="${PORT_FOLDER}/.update/ksa-macos-port"
[ -f "${PORT_FOLDER}/VERSION" ] && [ -f "${PORT_FOLDER}/port.sh" ] && [ -f "${PORT_FOLDER}/lib/common.sh" ] ||
    die "${PORT_FOLDER} doesn't look like a port folder; not replacing it."
ARGS=()
for a in "$@"; do
    [ "${a}" = --update ] || ARGS+=("${a}")
done
# bash goes on reading this script from the removed file, and the folder itself stays, so a Terminal in it stays there
for f in "${PORT_FOLDER}"/* "${PORT_FOLDER}"/.[!.]*; do
    if [ "${f}" != "${PORT_FOLDER}/.update" ] && { [ -e "${f}" ] || [ -L "${f}" ]; }; then
        rm -rf "${f}"
    fi
done
for f in "${NEW}"/*; do
    mv "${f}" "${PORT_FOLDER}/"
done
rm -rf "${PORT_FOLDER}/.update"
step "The port is now version $(version_file version)"
exec "${PORT_FOLDER}/port.sh" ${ARGS[@]+"${ARGS[@]}"}
