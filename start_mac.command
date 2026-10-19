#!/bin/bash
cd "$(dirname "$0")"

# Lokales MoltenVK (Ray-Query-Branch, siehe build_moltenvk.sh) als ICD-Treiber erzwingen
export VK_DRIVER_FILES="$(pwd)/MoltenVK_icd.json"
export VK_ICD_FILENAMES="$(pwd)/MoltenVK_icd.json"
export DYLD_LIBRARY_PATH="$(pwd):$DYLD_LIBRARY_PATH"

# GLFW-Shim laden (src/glfw_shim.c): niedrigere Auflösungen wie im Windows-Build unter Wine,
# keine "Raw mouse motion is not supported"-Fehler nach jedem Rechtsklick-Ziehen
if [ -f "$(pwd)/libGlfwShim.dylib" ]; then
    export DYLD_INSERT_LIBRARIES="$(pwd)/libGlfwShim.dylib"
fi

# Ray Queries im MoltenVK-Ray-Query-Branch einschalten (dort standardmäßig aus). Ohne sie platziert KSA
# das Ground Clutter (Gras) mit einer Software-BVH im Shader, was die FPS auf 0 drückt und die GPU hängen
# lassen kann. Sonst keine MVK_CONFIG_*-Overrides: MoltenVKs Standardwerte laufen mit KSA am besten.
export MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1

# Prüfen ob dotnet vorhanden ist
if ! command -v dotnet &> /dev/null; then
    echo "=========================================================="
    echo "FEHLER: .NET Runtime wurde nicht auf deinem Mac gefunden!"
    echo "Bitte installiere .NET 10 für macOS (Apple Silicon):"
    echo "  brew install --cask dotnet-sdk"
    echo "oder lade den offiziellen Mac-Installer herunter:"
    echo "  https://dotnet.microsoft.com/download/dotnet/10.0"
    echo "=========================================================="
    read -p "Drücke Enter zum Beenden..."
    exit 1
fi

echo "Starte Kitten Space Agency (macOS Native)..."
exec dotnet KSA.dll "$@"
