# The native part of the port: KSA Metal, the GLFW and KTX shims and the launcher, built from src/ with the
# dependencies declared in cmake/dependencies.cmake. build.sh runs it; on its own: cmake --preset default &&
# cmake --build --preset default
#
# Configuring also downloads everything else the port folder bundles (the .NET runtime, StarMap, BRUTAL's libraries)
# and writes deps.sh into the build tree, which tells build.sh's other steps where each download is.
cmake_minimum_required(VERSION 3.30)

# Everything compiled targets the oldest macOS the app supports, dependencies included: MACOS_MIN in
# src/port-folder/lib/common.sh. Otherwise a binary inherits the build Mac's version.
file(STRINGS src/port-folder/lib/common.sh macos_min REGEX "^MACOS_MIN=")
string(REGEX REPLACE "^MACOS_MIN=\"(.*)\"$" "\\1" MACOS_MIN "${macos_min}")
set(CMAKE_OSX_DEPLOYMENT_TARGET "${MACOS_MIN}")
# The app's identifier, which the launcher's signature names (src/launcher/)
file(STRINGS src/port-folder/lib/common.sh bundle_id REGEX "^BUNDLE_ID=")
string(REGEX REPLACE "^BUNDLE_ID=\"(.*)\"$" "\\1" BUNDLE_ID "${bundle_id}")
set(CMAKE_OSX_ARCHITECTURES arm64)

project(ksa-macos-port LANGUAGES C CXX OBJCXX)

# Where the app's libraries and the launcher go: those built here, BRUTAL's, and licences/ of everything compiled into
# them
set(KSA_NATIVES_DIR "${CMAKE_SOURCE_DIR}/deps/build/natives" CACHE PATH "Output folder for the app's libraries")

include(FetchContent)
# The dependencies' option() calls take the values set in cmake/dependencies.cmake
set(CMAKE_POLICY_DEFAULT_CMP0077 NEW)
include(cmake/dependencies.cmake)

FetchContent_MakeAvailable(spirv_headers)
set(SPIRV-Headers_SOURCE_DIR "${spirv_headers_SOURCE_DIR}")
FetchContent_MakeAvailable(vulkan_headers spirv_cross spirv_tools glfw_headers dotnet_runtime starmap starmap_license
    harmony_license lib_harmony title_card)

# Harmony for KSA Metal Hooks: the package's .NET 10 assembly. Extracted again only for another version, and then dated
# now: the hooks' build tells a changed reference by its date, and the package's own is older than the last build.
set(HARMONY_DIR "${CMAKE_BINARY_DIR}/harmony")
if(NOT EXISTS "${HARMONY_DIR}/${HARMONY_VERSION}.version")
    file(REMOVE_RECURSE "${HARMONY_DIR}")
    # By its name: the download folder keeps the package of an earlier version when the version changes
    file(ARCHIVE_EXTRACT INPUT "${lib_harmony_SOURCE_DIR}/lib.harmony.${HARMONY_VERSION}.nupkg"
        DESTINATION "${HARMONY_DIR}" PATTERNS "lib/net10.0/0Harmony.dll" TOUCH)
    file(TOUCH "${HARMONY_DIR}/${HARMONY_VERSION}.version")
endif()

# BRUTAL's native libraries, unmodified, with their licences
include(cmake/brutal.cmake)

# The licences of the sources compiled into the two libraries
foreach(source vulkan_headers spirv_cross spirv_tools spirv_headers)
    string(REPLACE "_" "-" folder "${source}")
    file(GLOB notices "${${source}_SOURCE_DIR}/LICENSE*" "${${source}_SOURCE_DIR}/NOTICE*"
        "${${source}_SOURCE_DIR}/COPYING*" "${${source}_SOURCE_DIR}/LICENSES")
    if(NOT notices)
        message(FATAL_ERROR "no licence file found in ${${source}_SOURCE_DIR}")
    endif()
    file(COPY ${notices} DESTINATION "${KSA_NATIVES_DIR}/licenses/${folder}")
endforeach()

add_subdirectory(src/ksa-metal)
add_subdirectory(src/glfw-shim)
add_subdirectory(src/ktx-shim)
add_subdirectory(src/launcher)

# For build.sh's other steps: where each download is (deps.sh)
include(cmake/deps-file.cmake)
