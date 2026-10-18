#!/bin/bash
# ==============================================================================
# Kitten Space Agency (KSA) - macOS .app bundle launcher
#
# Installed by make_app.sh as "Kitten Space Agency.app/Contents/MacOS/KSA".
# Puts the bundled .NET runtime (Contents/MacOS/dotnet) first on PATH and hands
# off to start_mac.command in Contents/Resources, which sets up MoltenVK and
# exec's the game, so the game process keeps this bundle's identity (Dock icon,
# menu bar name).
# ==============================================================================

CONTENTS="$(cd "$(dirname "$0")/.." && pwd)"
LOG_FILE="$HOME/Library/Logs/Kitten Space Agency.log"

# No terminal when launched from Finder: keep stdout/stderr in a log instead
mkdir -p "$(dirname "$LOG_FILE")"
exec >"$LOG_FILE" 2>&1

fail() {
    echo "ERROR: $1"
    osascript -e 'on run argv' -e 'display alert "Kitten Space Agency" message (item 1 of argv) as critical' -e 'end run' "$1" >/dev/null 2>&1
    exit 1
}

[ -x "$CONTENTS/MacOS/dotnet" ] || fail "The bundled .NET runtime is missing (Contents/MacOS/dotnet). Rebuild the app with make_app.sh."
[ -f "$CONTENTS/Resources/KSA.dll" ] || fail "The game files are missing (Contents/Resources/KSA.dll). Rebuild the app with make_app.sh."

export PATH="$CONTENTS/MacOS:$PATH"
export DOTNET_ROOT="$CONTENTS/MacOS"

# Drop the legacy Process Serial Number argument older macOS versions pass in
ARGS=()
for arg in "$@"; do
    [[ "$arg" == -psn_* ]] || ARGS+=("$arg")
done

echo "==> $(date): launching from $CONTENTS"
exec "$CONTENTS/Resources/start_mac.command" "${ARGS[@]}"
