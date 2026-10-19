#!/usr/bin/env python3
"""
Kitten Space Agency (KSA) - Ground Clutter Shader Fixes for Metal (MoltenVK)

KSA compiles its GLSL shaders at startup, so these fixes are applied to the
shader sources in Content/Core/Shaders/Planet/GroundClutter/ of a game folder:

1. PrefixSum.comp declares its ecotype uniform block by hand and leaves out the
   16-byte `TextureHandles textureHandles` member that the real EcotypeData
   layout (GroundClutterCommon.glsl, filled from ClutterCubeCellGrid.EcotypeData)
   has before `numObjects`. The shader then reads a texture index as the object
   count, and reads and writes past its LOD count/offset buffers. On Metal this
   corrupts clutter data and can hang the GPU, worst with clutter shadows on.
   Fix: add the missing member so the layout matches.

2. Solid.vert (colour pass) and SolidDepth.vert (depth pre-pass) must produce
   bit-identical positions, because the colour pass draws with depth compare
   EQUAL. Without `invariant gl_Position`, Metal's fast math computes them
   slightly differently, which shows up as black hatched stripes or see-through
   patches on clutter. Fix: declare gl_Position invariant in both shaders.

The script is idempotent, keeps each file's line endings (the shaders use CRLF),
and warns instead of failing when a game update has changed the code around a fix.

Usage: python3 patch_shaders.py /path/to/game
"""

import os
import re
import sys

GAME_DIR = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.getcwd()
SHADER_DIR = os.path.join(GAME_DIR, "Content", "Core", "Shaders", "Planet", "GroundClutter")
COMMENT = "// macOS port: "

if not os.path.isdir(SHADER_DIR):
    print(f"Error: {SHADER_DIR} not found - is this a KSA game folder?")
    sys.exit(1)


def fix_prefix_sum(text, nl):
    if re.search(r"^\s*TextureHandles\s+textureHandles\s*;", text, re.M):
        return None, "already has textureHandles"
    old = f"    PlacementParams placementParams;{nl}    uint numObjects;{nl}"
    if text.count(old) != 1:
        return None, "uniform block not found as expected (fixed upstream?) - check by hand"
    new = (f"    PlacementParams placementParams;{nl}"
           f"    TextureHandles textureHandles;  {COMMENT}was missing; must match EcotypeData (GroundClutterCommon.glsl){nl}"
           f"    uint numObjects;{nl}")
    return text.replace(old, new), "added TextureHandles textureHandles"


def fix_invariant_position(text, nl):
    if re.search(r"^\s*invariant\s+gl_Position\s*;", text, re.M):
        return None, "already declares gl_Position invariant"
    mains = list(re.finditer(r"^void main\(\)", text, re.M))
    if len(mains) != 1:
        return None, "main() not found as expected - check by hand"
    insert = (f"{COMMENT}depth pre-pass and colour pass (depth EQUAL) need bit-identical positions on Metal{nl}"
              f"invariant gl_Position;{nl}{nl}")
    at = mains[0].start()
    return text[:at] + insert + text[at:], "declared gl_Position invariant"


FIXES = [
    ("PrefixSum.comp", fix_prefix_sum),
    ("Solid.vert", fix_invariant_position),
    ("SolidDepth.vert", fix_invariant_position),
]

for name, fix in FIXES:
    path = os.path.join(SHADER_DIR, name)
    if not os.path.isfile(path):
        print(f"[WARN]    {name}: not found - skipped")
        continue
    with open(path, "rb") as f:
        raw = f.read()
    nl = "\r\n" if b"\r\n" in raw else "\n"
    patched, status = fix(raw.decode("utf-8"), nl)
    if patched is None:
        tag = "[OK]     " if status.startswith("already") else "[WARN]   "
        print(f"{tag} {name}: {status}")
        continue
    with open(path, "wb") as f:
        f.write(patched.encode("utf-8"))
    print(f"[SHADER]  {name}: {status}")
