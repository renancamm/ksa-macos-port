#!/bin/bash
# Writes the commented template of port-settings.toml, the copy the launcher puts in the player's documents folder,
# from the table of the port settings (src/launcher/port-settings.txt, which explains its columns).
#
# Usage: scripts/make-port-settings.sh <table> > port-settings.toml
set -eo pipefail
[ -f "$1" ] || { echo "usage: scripts/make-port-settings.sh <table>" >&2; exit 1; }

cat << 'EOF'
# Port settings for Kitten Space Agency (ksa-macos-port)
#
# The Mac Port tab in the game's settings changes these too, and says which take effect at once and which when the
# game starts again. A line you delete takes its default (the value written here); delete the file to get this one
# back. Messages about the file go to logs/console.log in this folder.
EOF

awk -F'|' '
    function trim(s) { gsub(/^[ \t]+|[ \t]+$/, "", s); return s }
    # Wraps text into "# " comment lines of at most 118 columns
    function comment(text,    n, words, i, line) {
        n = split(text, words, " ")
        line = "#"
        for (i = 1; i <= n; i++) {
            if (length(line) + 1 + length(words[i]) > 118) { print line; line = "#" }
            line = line " " words[i]
        }
        print line
    }
    # The values a type takes, as the comment shows them
    function values(type,    n, parts, choices, i, eq, out) {
        if (type == "bool" || type == "available") return "true or false"
        if (type == "text") return "a path or text, \"\" for none"
        n = split(type, parts, ":")
        if (parts[1] == "choice") {
            n = split(parts[2], choices, "/")
            for (i = 1; i <= n; i++) {
                eq = index(choices[i], "=")
                out = out (i == 1 ? "" : (i == n ? " or " : ", ")) "\"" substr(choices[i], 1, eq - 1) "\""
            }
            return out
        }
        return parts[2] " to " parts[3]
    }
    function toml(type, value) {
        if (type ~ /^(choice|text)/) return "\"" value "\""
        return value
    }
    /^\[/ {
        section = substr($0, 2, index($0, "]") - 2)
        printf "\n[%s]\n", section
        if (section == "developer") {
            print ""
            comment("For work on the port, and for testing a problem: KSA Metal'"'"'s README in the port'"'"'s repository " \
                "(src/ksa-metal/) has the details. Remove the # in front of a line to use it.")
        }
        next
    }
    /^[ \t]*(#|$)/ { next }
    {
        key = trim($1); type = trim($2); def = trim($3); help = trim($9)
        print ""
        comment(key " (" values(type) "): " help)
        printf "%s%s = %s\n", (section == "developer" ? "# " : ""), key, toml(type, def)
    }
' "$1"
