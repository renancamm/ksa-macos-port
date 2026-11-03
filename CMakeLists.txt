# The whole build of the port, with every download declared in cmake/dependencies.cmake: KSA Metal, the GLFW and KTX
# shims and the launcher; the files made from the settings table and Vulkan's registry; the .NET projects; the app's
# icon. `cmake --install` assembles the port folder (cmake/port-folder.cmake), and ctest runs the tests. build.sh runs
# the four steps, with the KSA the hooks and the benchmark are built against:
#
#   cmake --preset default -DKSA_DIR=<folder with KSA.dll>
#   cmake --build --preset default
#   cmake --install deps/build/cmake --prefix dist/ksa-macos-port
#   ctest --preset default
cmake_minimum_required(VERSION 3.30)

# The port's names (src/player/port-folder/lib/port.env), each KEY="value": MACOS_MIN, the oldest macOS the app
# supports, which everything compiled targets, dependencies included (otherwise a binary inherits the build Mac's
# version), and BUNDLE_ID, the app's identifier, which the launcher's signature names (src/game/launcher/)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS src/player/port-folder/lib/port.env)
file(STRINGS src/player/port-folder/lib/port.env port_names REGEX "^[A-Z_]+=\"")
foreach(line ${port_names})
    string(REGEX REPLACE "^([A-Z_]+)=\"(.*)\"$" "\\1" name "${line}")
    string(REGEX REPLACE "^([A-Z_]+)=\"(.*)\"$" "\\2" "${name}" "${line}")
endforeach()
if(NOT MACOS_MIN OR NOT BUNDLE_ID)
    message(FATAL_ERROR "src/player/port-folder/lib/port.env gives no MACOS_MIN or BUNDLE_ID")
endif()
set(CMAKE_OSX_DEPLOYMENT_TARGET "${MACOS_MIN}")
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
if(NOT EXISTS "${HARMONY_DIR}/${HARMONY_VERSION}.version" OR NOT EXISTS "${HARMONY_DIR}/lib/net10.0/0Harmony.dll")
    file(REMOVE_RECURSE "${HARMONY_DIR}")
    # By its name: the download folder keeps the package of an earlier version when the version changes
    file(ARCHIVE_EXTRACT INPUT "${lib_harmony_SOURCE_DIR}/lib.harmony.${HARMONY_VERSION}.nupkg"
        DESTINATION "${HARMONY_DIR}" PATTERNS "lib/net10.0/0Harmony.dll" TOUCH)
    file(TOUCH "${HARMONY_DIR}/${HARMONY_VERSION}.version")
endif()

# port_folder_copy(<destination> <file or folder/>...): copies them into the port folder on `cmake --install`, as they
# are (permissions, links); a folder with a trailing slash copies its contents
function(port_folder_copy destination)
    set(quoted "")
    foreach(each IN LISTS ARGN)
        string(APPEND quoted " \"${each}\"")
    endforeach()
    install(CODE "file(COPY ${quoted} DESTINATION \"\${CMAKE_INSTALL_PREFIX}/${destination}\")")
endfunction()
# Where the app's files go in the port folder: the app without the game
set(PORT_APP "payload/app/Contents")

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
# Those and BRUTAL's, in the app's licences
port_folder_copy("${PORT_APP}/Resources/Licenses" "${KSA_NATIVES_DIR}/licenses/")

# The .NET SDK, as the build runs it: its state and the packages it restores in the build tree. The projects' output
# goes to KSA_ASSEMBLIES_DIR (bin/<project>/), what the build's tools make to the build tree's generated/.
find_program(DOTNET dotnet REQUIRED)
set(KSA_ASSEMBLIES_DIR "${CMAKE_SOURCE_DIR}/deps/build/assemblies" CACHE PATH "Output folder of the .NET projects")
set(KSA_GENERATED_DIR "${CMAKE_BINARY_DIR}/generated")
# One .NET command at a time: the projects share bin/ and obj/ folders (KsaPort.Common's among them), and a tool's
# build or run must not meet the solution's
set_property(GLOBAL APPEND PROPERTY JOB_POOLS dotnet=1)
set(PORT_DOTNET "${CMAKE_COMMAND}" -E env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 "DOTNET_CLI_HOME=${KSA_ASSEMBLIES_DIR}/home"
    "NUGET_PACKAGES=${KSA_ASSEMBLIES_DIR}/nuget" "${DOTNET}")
# No PDB: the port folder doesn't ship them, and a DLL built with one records its path
set(PORT_DOTNET_BUILD -c Release -nologo -v quiet "-p:PortBuildDir=${KSA_ASSEMBLIES_DIR}" -p:DebugType=none
    -nodeReuse:false)
# port_dotnet_tool(<variable> <project> [<referenced project or source>...]): builds a .NET program the build runs
# when its sources or those of the projects it references change, and sets <variable> to its assembly
function(port_dotnet_tool variable project)
    get_filename_component(name "${project}" NAME_WLE)
    set(assembly "${KSA_ASSEMBLIES_DIR}/bin/${name}/${name}.dll")
    set(sources "${PROJECT_SOURCE_DIR}/src/Directory.Build.props")
    foreach(each "${project}" ${ARGN})
        get_filename_component(folder "${each}" DIRECTORY)
        # A project's own sources: those of its folder, but what an editor's build leaves and its tests' project
        file(GLOB_RECURSE found RELATIVE "${folder}" CONFIGURE_DEPENDS "${folder}/*.cs")
        list(FILTER found EXCLUDE REGEX "^(bin|obj|tests)/")
        list(TRANSFORM found PREPEND "${folder}/")
        list(APPEND sources "${each}" ${found})
    endforeach()
    add_custom_command(OUTPUT "${assembly}"
        COMMAND ${PORT_DOTNET} build "${project}" ${PORT_DOTNET_BUILD}
        # MSBuild leaves an assembly whose code is the same as it was: dated now, it marks the tool as built
        COMMAND "${CMAKE_COMMAND}" -E touch "${assembly}"
        DEPENDS ${sources}
        JOB_POOL dotnet
        COMMENT "Building ${name}" VERBATIM)
    set(${variable} "${assembly}" PARENT_SCOPE)
endfunction()

# The components, each with its README; ctest runs the tests some of them have
enable_testing()
# The port settings' variables and defaults for the native code: port-settings.h, which the settings' generator makes
# from the table (src/shared/settings/). It writes the header only when it differs, so the code that includes it is
# compiled again only then; the stamp marks the table as made.
port_dotnet_tool(SETTINGS_GENERATOR
    "${PROJECT_SOURCE_DIR}/src/build-tools/settings-generator/KsaPort.Tools.SettingsGenerator.csproj"
    "${PROJECT_SOURCE_DIR}/src/shared/KsaPort.Common.csproj")
set(settings_table "${PROJECT_SOURCE_DIR}/src/shared/settings/port-settings.txt")
add_custom_command(OUTPUT "${KSA_GENERATED_DIR}/port-settings.stamp"
    BYPRODUCTS "${KSA_GENERATED_DIR}/port-settings.h"
    COMMAND "${DOTNET}" "${SETTINGS_GENERATOR}" header "${settings_table}" "${KSA_GENERATED_DIR}/port-settings.h"
    COMMAND "${CMAKE_COMMAND}" -E touch "${KSA_GENERATED_DIR}/port-settings.stamp"
    DEPENDS "${settings_table}" "${SETTINGS_GENERATOR}"
    JOB_POOL dotnet
    COMMENT "Making port-settings.h from the settings table" VERBATIM)
add_custom_target(port-settings-header DEPENDS "${KSA_GENERATED_DIR}/port-settings.stamp")
add_library(port-settings INTERFACE)
target_include_directories(port-settings INTERFACE "${KSA_GENERATED_DIR}")
# Followed by every target that links it
add_dependencies(port-settings port-settings-header)
add_subdirectory(src/game/driver)
add_subdirectory(src/game/shims/glfw)
add_subdirectory(src/game/shims/ktx)
add_subdirectory(src/game/launcher)

# The .NET projects, the port folder and the tests of the port as a whole
include(cmake/port-folder.cmake)
