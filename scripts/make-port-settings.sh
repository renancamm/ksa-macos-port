#!/bin/bash
# Writes the commented template of port-settings.toml, the copy the hooks put in the player's documents folder,
# from the table of the port settings (src/launcher/port-settings.txt, which explains its columns).
#
# Usage: scripts/make-port-settings.sh <table> > port-settings.toml
set -eo pipefail
[ -f "$1" ] || { echo "usage: scripts/make-port-settings.sh <table>" >&2; exit 1; }

cat << 'EOF'
# Port settings for Kitten Space Agency (ksa-macos-port)
#
# The Mac Port tab in the game's settings (Esc) changes these too, and says which take effect at once and which when
# the game starts again. A line you delete takes its default (the value written here); delete the file to get this
# one back. Messages about the file go to logs/console.log in this folder.
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
        if (type == "bool") return "true or false"
        if (type == "text") return "a path or text, \"\" for none"
        n = split(type, parts, ":")
        if (parts[1] == "numbers")
            return (parts[2] == parts[3] ? parts[2] : parts[2] " to " parts[3]) " numbers, comma separated, \"\" for none"
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
        if (type ~ /^(choice|text|numbers)/) return "\"" value "\""
        return value
    }
    # A setting is written once the lines with what its values mean have been read
    function flush() {
        if (key == "") return
        print ""
        comment(key " (" values(type) "): " help)
        printf "%s%s = %s\n", (section == "developer" ? "# " : ""), key, toml(type, def)
        key = ""
    }
    /^\[/ {
        flush()
        section = substr($0, 2, index($0, "]") - 2)
        printf "\n[%s]\n", section
        if (index($0, "|") > 0) {
            print ""
            comment(trim(substr($0, index($0, "|") + 1)))
        }
        if (section == "developer") {
            print ""
            comment("For work on the port, and for testing a problem: the driver'"'"'s README in the port'"'"'s repository " \
                "(src/ksa-metal/) explains them. Remove the # in front of a line to use it.")
        }
        next
    }
    /^[ \t]*(#|$)/ { next }
    /^[ \t]/ {
        help = help " \"" trim($1) "\": " trim($2)
        next
    }
    {
        flush()
        key = trim($1); type = trim($2); def = trim($3); help = trim($9)
    }
    END { flush() }
' "$1"
