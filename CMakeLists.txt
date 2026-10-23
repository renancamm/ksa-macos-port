# The native part of the port: KSA Metal and the GLFW shim, built from src/ with the dependencies declared in
# cmake/dependencies.cmake. build.sh runs it; on its own: cmake --preset default && cmake --build --preset default
#
# Configuring also downloads everything else the port folder bundles (the .NET runtime, StarMap, BRUTAL's libraries)
# and writes deps.sh into the build tree, which tells build.sh's other steps where each download is.
cmake_minimum_required(VERSION 3.30)

# Everything compiled targets the oldest macOS the app supports, dependencies included: MACOS_MIN in
# src/port/lib/common.sh. Otherwise a binary inherits the build Mac's version.
file(STRINGS src/port/lib/common.sh macos_min REGEX "^MACOS_MIN=")
string(REGEX REPLACE "^MACOS_MIN=\"(.*)\"$" "\\1" MACOS_MIN "${macos_min}")
set(CMAKE_OSX_DEPLOYMENT_TARGET "${MACOS_MIN}")
set(CMAKE_OSX_ARCHITECTURES arm64)

project(ksa-macos-port LANGUAGES C CXX OBJCXX)

# Where the libraries for the app go: the two built here, BRUTAL's, and licences/ of everything compiled into them
set(KSA_NATIVES_DIR "${CMAKE_SOURCE_DIR}/deps/build/natives" CACHE PATH "Output folder for the app's libraries")

include(FetchContent)
# The dependencies' option() calls take the values set in cmake/dependencies.cmake
set(CMAKE_POLICY_DEFAULT_CMP0077 NEW)
include(cmake/dependencies.cmake)

FetchContent_MakeAvailable(spirv_headers)
set(SPIRV-Headers_SOURCE_DIR "${spirv_headers_SOURCE_DIR}")
FetchContent_MakeAvailable(vulkan_headers spirv_cross spirv_tools glfw_headers dotnet_runtime starmap starmap_license
    harmony_license title_card)

# BRUTAL's native libraries: RocketWerkz's macOS builds, unmodified, from each package's runtimes/osx-arm64/native/,
# with BRUTAL's licence, notice and the packages' third-party notices. Some packages ship one library under several
# names, as identical copies (libglfw.dylib, libglfw.3.dylib, libglfw.3.5.dylib): the copies become links to the one
# named by the library's install name, so that the game and the GLFW shim, which links that name, load the same GLFW.
# The folder starts anew when the list of packages changes, so no library of an older package stays behind.
if(NOT "${BRUTAL_PACKAGES}" STREQUAL "$CACHE{KSA_BRUTAL_STAGED}")
    file(REMOVE_RECURSE "${KSA_NATIVES_DIR}")
endif()
file(MAKE_DIRECTORY "${KSA_NATIVES_DIR}/licenses/brutal")
foreach(entry IN LISTS BRUTAL_PACKAGES)
    string(REGEX MATCHALL "[^ ]+" fields "${entry}")
    list(GET fields 0 id)
    string(TOLOWER "${id}" lower)
    string(REPLACE "." "_" name "${lower}")
    FetchContent_MakeAvailable(${name})
    set(unpacked "${CMAKE_BINARY_DIR}/brutal/${name}")
    file(REMOVE_RECURSE "${unpacked}")
    file(GLOB package "${${name}_SOURCE_DIR}/*.nupkg")
    file(ARCHIVE_EXTRACT INPUT "${package}" DESTINATION "${unpacked}"
        PATTERNS "runtimes/osx-arm64/native/*" LICENSE.txt NOTICE.txt "LICENSES/*")
    file(GLOB natives "${unpacked}/runtimes/osx-arm64/native/*.dylib")
    if(NOT natives)
        message(FATAL_ERROR "${id} has no macOS libraries.")
    endif()
    file(COPY ${natives} DESTINATION "${KSA_NATIVES_DIR}")
    foreach(native IN LISTS natives)
        get_filename_component(file_name "${native}" NAME)
        file(STRINGS "${native}" install_name REGEX "@rpath/[A-Za-z0-9._+-]+\\.dylib")
        string(REGEX MATCH "@rpath/[A-Za-z0-9._+-]+\\.dylib" install_name "${install_name}")
        string(REPLACE "@rpath/" "" install_name "${install_name}")
        if(install_name AND NOT install_name STREQUAL file_name AND EXISTS "${unpacked}/runtimes/osx-arm64/native/${install_name}")
            file(SHA256 "${native}" copy)
            file(SHA256 "${unpacked}/runtimes/osx-arm64/native/${install_name}" original)
            if(copy STREQUAL original)
                file(REMOVE "${KSA_NATIVES_DIR}/${file_name}")
                file(CREATE_LINK "${install_name}" "${KSA_NATIVES_DIR}/${file_name}" SYMBOLIC)
            endif()
        endif()
    endforeach()
    file(GLOB notices "${unpacked}/LICENSE.txt" "${unpacked}/NOTICE.txt" "${unpacked}/LICENSES/*")
    file(COPY ${notices} DESTINATION "${KSA_NATIVES_DIR}/licenses/brutal")
endforeach()
set(KSA_BRUTAL_STAGED "${BRUTAL_PACKAGES}" CACHE INTERNAL "The BRUTAL packages in KSA_NATIVES_DIR")

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

# For build.sh's other steps: where each download is, and the versions the port folder records
set(brutal_list "")
foreach(entry IN LISTS BRUTAL_PACKAGES)
    string(REGEX MATCHALL "[^ ]+" fields "${entry}")
    list(GET fields 0 id)
    list(GET fields 1 version)
    string(APPEND brutal_list "    \"${id} ${version}\"\n")
endforeach()
file(GLOB title_card "${title_card_SOURCE_DIR}/*")
file(CONFIGURE OUTPUT "${CMAKE_BINARY_DIR}/deps.sh" CONTENT [[
# Written by CMakeLists.txt from cmake/dependencies.cmake: where each download is. Sourced by scripts/lib.sh.
NATIVES="${KSA_NATIVES_DIR}"
DOTNET_VERSION="${DOTNET_VERSION}"
DOTNET_RUNTIME="${dotnet_runtime_SOURCE_DIR}"
STARMAP_VERSION="${STARMAP_VERSION}"
STARMAP="${starmap_SOURCE_DIR}"
STARMAP_LICENSE="${starmap_license_SOURCE_DIR}/LICENSE.txt"
HARMONY_LICENSE="${harmony_license_SOURCE_DIR}/LICENSE"
TITLE_CARD="${title_card}"
TITLE_CARD_URL="${TITLE_CARD_URL}"
BRUTAL_PACKAGES=(
${brutal_list})
]])
