# The shader edits

KSA compiles its GLSL shaders from `Content/Core/Shaders/` when it starts, so the port's changes to them go into the
sources, at port time. Each `.edits` file here holds the edits of one subject, with a comment that explains them; the
porter makes them in this order:

| Folder | Holds | Without them |
|---|---|---|
| [`fixes/`](fixes/) | bugs the game needs fixed on the port | the game breaks: GPU hangs, clutter corrupted or striped |
| [`features/`](features/) | what a port setting needs: upscaling's render scale, Simpler PCSS Shadows, Baked Milky Way | the setting has no effect; with it off, the edits change nothing |
| [`optimizations/`](optimizations/) | the same image, faster: Skip Shadows on Unlit Sides, the clouds' noise | the game's own shaders, slower |

Within a folder the files go in the order of their names. An edit that can't be made (the code around it changed in
a KSA update) prints `[WARN]` and leaves the game's code.

## The format

A file starts with its comment (`#` lines). Then steps, each started by a line `@@ <kind> <id> <shader>`, the shader's
path under `Content/Core/Shaders/`, and followed by its parts, each a line `@@ <part> [<value>]`. A part with text takes
the lines after it up to the next `@@` line, verbatim, without their trailing empty lines: no text ends with a line
break, and one that starts with an empty line starts with one. A comment between steps is a line `@@ # <text>`.

```text
@@ edit light-cluster-inverted-range Lighting/LightFineClusterCompact.comp
@@ what skipped tiles with an inverted depth range
@@ find
    uint binCount = maxBin - minBin + 1u;
@@ replace
    if (maxBin < minBin) return;  // macOS port: an inverted depth range would loop for billions of bins
    uint binCount = maxBin - minBin + 1u;
```

| Part | Of | Means |
|---|---|---|
| `@@ what <text>` | every step | what the edit did, as its `[FIXED]` line says; for a check, what is left out when it fails |
| `@@ needs <id>...` | every step | made only when those steps held (`[FIXED]` or `[OK]`); otherwise `[SKIP]` |
| `@@ find` | edit | the text to replace: exactly once in the shader |
| `@@ replace` | edit | its replacement; a `// macOS port:` comment says why, unless the change speaks for itself |
| `@@ earlier` | edit | what an earlier version of the port made of `find`: replaced too, in an app ported with it |
| `@@ done-if <text>` | edit | the shader holds this already (the game fixed it, or another wording of the edit): `[OK]` |
| `@@ done-if-line <text>` | edit | as `done-if`, for a line that starts with this text (after its indentation): not in a comment |
| `@@ contains [<shader>]` | check | text the shader, or another one, must contain |
| `@@ counts [<shader>] <word>=<n>[,<n>...]...` | check | how often each word may appear: the first number in the game's file, any of them once the file holds the shadow edits (`!PORT_SKIP_UNLIT_SHADOWS`) |

An edit goes, in this order: its replacement there already is `[OK]`; an earlier version's replacement there once is
replaced (`[FIXED] updated:`); `done-if` (or `done-if-line`) there is `[OK]`; `find` there once is replaced (`[FIXED]`); anything else is
`[WARN]`. Edits keep a shader's line endings (CRLF) and every other byte. A **check** makes sure the code that uses what
an edit skips still reads as it does here; it prints only when it fails (`[WARN]`), and the edits that need it are then
skipped. A **code** step (`@@ code <id> <shader>`) is an edit the porter's code makes: `milky-way`
([`../MilkyWay.cs`](../MilkyWay.cs)) moves a whole block of the shader.

## Writing an edit

1. Find the smallest text that identifies the place, exactly once in the shader.
2. For an edit that skips work, add a check of the code that uses what it skips, and make the edit need it: a KSA
   version that changed that code keeps the game's image.
3. For an edit that needs another (a declaration), make it need that one: without it the shader would not compile.
4. Port the game into a test app and run it again: the first run prints `[FIXED]`, the second only `[OK]`.
5. When an edit's replacement changes, keep the old one as `@@ earlier`, so an app ported with it gets the new one.
