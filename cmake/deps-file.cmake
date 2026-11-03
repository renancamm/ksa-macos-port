# deps.sh in the build tree, included by CMakeLists.txt once every download is made available: where each download is,
# and the versions the port folder records. scripts/lib.sh sources it (load_deps).
set(brutal_list "")
foreach(entry IN LISTS BRUTAL_PACKAGES)
    string(REGEX MATCHALL "[^ ]+" fields "${entry}")
    list(GET fields 0 id)
    list(GET fields 1 version)
    string(APPEND brutal_list "    \"${id} ${version}\"\n")
endforeach()
file(GLOB title_card "${title_card_SOURCE_DIR}/*")
file(CONFIGURE OUTPUT "${CMAKE_BINARY_DIR}/deps.sh" CONTENT [[
# Written by cmake/deps-file.cmake from cmake/dependencies.cmake: where each download is. Sourced by scripts/lib.sh.
NATIVES="${KSA_NATIVES_DIR}"
DOTNET_VERSION="${DOTNET_VERSION}"
DOTNET_RUNTIME="${dotnet_runtime_SOURCE_DIR}"
STARMAP_VERSION="${STARMAP_VERSION}"
STARMAP="${starmap_SOURCE_DIR}"
STARMAP_LICENSE="${starmap_license_SOURCE_DIR}/LICENSE.txt"
HARMONY_LICENSE="${harmony_license_SOURCE_DIR}/LICENSE"
HARMONY_VERSION="${HARMONY_VERSION}"
HARMONY_DLL="${HARMONY_DIR}/lib/net10.0/0Harmony.dll"
TITLE_CARD="${title_card}"
TITLE_CARD_URL="${TITLE_CARD_URL}"
BRUTAL_PACKAGES=(
${brutal_list})
]])
