# The port folder, dist/ksa-macos-port/ (`cmake --install`): everything the port needs but the game. The native
# components install their own files (port_folder_copy in their CMakeLists.txt) and BRUTAL's libraries theirs
# (brutal.cmake); this file builds the .NET projects, installs them, the commands, the downloads the folder bundles,
# the icon and the licences, and adds the tests of the port as a whole. App.cs (src/player/porter/) describes the
# app's layout.

# What the build is made for: the KSA the hooks and the benchmark are compiled against (build.sh finds it: the
# installed app's, or a copy of an archive's assemblies, which still have the Linux .NET runtime's own and are
# converted here first, as port.sh converts a game), its version, and what VERSION says of the port
set(default_ksa "$ENV{HOME}/Applications/${APP_NAME}.app/Contents/Resources")
set(KSA_DIR "${default_ksa}" CACHE PATH "The folder with the KSA.dll the hooks and the benchmark are built against")
set(PORT_VERSION "dev" CACHE STRING "The port's version, which VERSION records: dev, or a release's")
set(PORT_COMMIT "unknown" CACHE STRING "The commit the port is built from, which VERSION records")
set(PORT_FOLDER "${PROJECT_SOURCE_DIR}/dist/ksa-macos-port" CACHE PATH
    "The port folder: cmake --install's default prefix, and what the port folder's tests check")
if(CMAKE_INSTALL_PREFIX_INITIALIZED_TO_DEFAULT)
    set(CMAKE_INSTALL_PREFIX "${PORT_FOLDER}" CACHE PATH "Where cmake --install puts the port folder" FORCE)
endif()
if(NOT EXISTS "${KSA_DIR}/KSA.dll")
    message(FATAL_ERROR "KSA_DIR (${KSA_DIR}) has no KSA.dll to build the hooks and the benchmark against. "
        "./build.sh finds one: the installed app, or an archive of KSA's Linux version.")
endif()
set(convert OFF)
if(EXISTS "${KSA_DIR}/System.Private.CoreLib.dll")
    set(convert ON)
endif()
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${KSA_DIR}/KSA.dll" "${PROJECT_SOURCE_DIR}/global.json")
file(STRINGS "${KSA_DIR}/KSA.dll" ksa_version REGEX "20[0-9][0-9]\\.[0-9]+\\.[0-9]+\\.[0-9]+" LIMIT_COUNT 1)
string(REGEX MATCH "20[0-9][0-9]\\.[0-9]+\\.[0-9]+\\.[0-9]+" KSA_VERSION "${ksa_version}")
if(NOT EXISTS "${starmap_SOURCE_DIR}/StarMap.dll" OR NOT EXISTS "${starmap_SOURCE_DIR}/StarMap.Core.dll")
    message(FATAL_ERROR "the StarMap archive has an unexpected layout (${starmap_SOURCE_DIR})")
endif()

# The port folder's files made here: VERSION, the BRUTAL packages the porter checks the game's assemblies against,
# and the port's names next to them (for the conversion below, which reads the folder's lib/)
set(stage "${CMAKE_BINARY_DIR}/port-folder")
execute_process(COMMAND "${DOTNET}" --version WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
    OUTPUT_VARIABLE dotnet_sdk OUTPUT_STRIP_TRAILING_WHITESPACE COMMAND_ERROR_IS_FATAL ANY)
file(CONFIGURE OUTPUT "${stage}/VERSION" @ONLY
    CONTENT "version=@PORT_VERSION@\nksa=@KSA_VERSION@\ncommit=@PORT_COMMIT@\ndotnet_sdk=@dotnet_sdk@\n")
set(brutal_list "")
foreach(entry IN LISTS BRUTAL_PACKAGES)
    string(REGEX MATCHALL "[^ ]+" fields "${entry}")
    list(GET fields 0 id)
    list(GET fields 1 version)
    string(APPEND brutal_list "${id} ${version}\n")
endforeach()
file(CONFIGURE OUTPUT "${stage}/lib/brutal.txt" CONTENT "${brutal_list}" @ONLY)
configure_file(src/player/port-folder/lib/port.env "${stage}/lib/port.env" COPYONLY)

# The app's icon, from KSA's title card
file(GLOB title_card "${title_card_SOURCE_DIR}/*")
set(icon "${CMAKE_BINARY_DIR}/icon")
add_custom_command(OUTPUT "${icon}/AppIcon.icns" "${icon}/NOTICE.txt"
    COMMAND bash "${PROJECT_SOURCE_DIR}/scripts/build-icon.sh" "${title_card}" "${TITLE_CARD_URL}" "${icon}"
    DEPENDS "${title_card}" "${PROJECT_SOURCE_DIR}/scripts/build-icon.sh"
    COMMENT "Making the app icon" VERBATIM)
add_custom_target(icon ALL DEPENDS "${icon}/AppIcon.icns")

# port-settings.toml's commented template, which the hooks copy for a new player
add_custom_command(OUTPUT "${KSA_GENERATED_DIR}/port-settings.toml"
    COMMAND sh -c "\"$0\" \"$1\" template \"$2\" > \"$3\"" "${DOTNET}" "${SETTINGS_GENERATOR}" "${settings_table}"
        "${KSA_GENERATED_DIR}/port-settings.toml"
    DEPENDS "${settings_table}" "${SETTINGS_GENERATOR}"
    JOB_POOL dotnet
    COMMENT "Making port-settings.toml's template" VERBATIM)
add_custom_target(port-settings-template ALL DEPENDS "${KSA_GENERATED_DIR}/port-settings.toml")

# The repository against the settings table (the generator's checks, src/shared/settings/README.md): every build,
# before the .NET projects, whose compile errors would say less
add_custom_target(settings-checks ALL
    COMMAND "${DOTNET}" "${SETTINGS_GENERATOR}" check "${PROJECT_SOURCE_DIR}"
    DEPENDS "${SETTINGS_GENERATOR}"
    JOB_POOL dotnet
    COMMENT "Checking the repository against the settings table" VERBATIM)

# The .NET projects (ksa-macos-port.slnx), against KSA_DIR's assemblies and Harmony; the hooks and the director are
# built for that KSA version, and the hooks skip what moved in another. An archive's assemblies are converted first
# (the porter's convert-dotnet), as port.sh converts a game.
set(harmony "${HARMONY_DIR}/lib/net10.0/0Harmony.dll")
set(ksa_input "${KSA_DIR}/KSA.dll")
set(keep_porter "")
if(convert)
    # The porter compiles the changes' port/ code: every .cs of src/game/changes/ counts
    port_dotnet_tool(PORTER "${PROJECT_SOURCE_DIR}/src/player/porter/KsaPort.Porter.csproj"
        "${PROJECT_SOURCE_DIR}/src/shared/KsaPort.Common.csproj" "${PROJECT_SOURCE_DIR}/src/game/changes/README.md")
    # The solution's build keeps the porter it converted with, so a failed build converts nothing twice
    set(keep_porter ! -name KsaPort.Porter)
    set(ksa_input "${KSA_DIR}/.converted")
    add_custom_command(OUTPUT "${ksa_input}"
        COMMAND "${CMAKE_COMMAND}" -E env "KSA_PORT_FOLDER=${stage}" "${DOTNET}" "${PORTER}" convert-dotnet "${KSA_DIR}"
            "${dotnet_runtime_SOURCE_DIR}"
        COMMAND "${CMAKE_COMMAND}" -E touch "${ksa_input}"
        DEPENDS "${PORTER}" "${KSA_DIR}/KSA.dll" "${stage}/lib/brutal.txt"
        JOB_POOL dotnet
        COMMENT "Converting KSA's assemblies" VERBATIM)
endif()
file(GLOB_RECURSE dotnet_sources RELATIVE "${PROJECT_SOURCE_DIR}/src" CONFIGURE_DEPENDS
    "src/*.cs" "src/*.csproj" "src/*.props" "src/*.edits")
list(FILTER dotnet_sources EXCLUDE REGEX "(^|/)(bin|obj)/")
list(TRANSFORM dotnet_sources PREPEND "${PROJECT_SOURCE_DIR}/src/")
set(bin "${KSA_ASSEMBLIES_DIR}/bin")
set(solution_stamp "${KSA_ASSEMBLIES_DIR}/solution.stamp")
add_custom_command(OUTPUT "${solution_stamp}"
    # The output starts anew but the build's tools', so the port folder gets nothing an earlier build left; obj/ stays,
    # so only what changed is compiled again
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${bin}"
    COMMAND find "${bin}" -mindepth 1 -maxdepth 1 ! -name KsaPort.Tools.EntriesGenerator
        ! -name KsaPort.Tools.SettingsGenerator ${keep_porter} -exec rm -rf {} +
    COMMAND ${PORT_DOTNET} build "${PROJECT_SOURCE_DIR}/ksa-macos-port.slnx" ${PORT_DOTNET_BUILD} "-p:KsaDir=${KSA_DIR}"
        "-p:HarmonyDll=${harmony}"
    COMMAND "${CMAKE_COMMAND}" -E touch "${solution_stamp}"
    DEPENDS ${dotnet_sources} "${settings_table}" "${PROJECT_SOURCE_DIR}/ksa-macos-port.slnx"
        "${PROJECT_SOURCE_DIR}/global.json" "${harmony}" "${ksa_input}"
    JOB_POOL dotnet
    COMMENT "Building the .NET projects against KSA ${KSA_VERSION}" VERBATIM)
add_custom_target(dotnet ALL DEPENDS "${solution_stamp}")
# After the settings' checks and the build's tools
add_dependencies(dotnet settings-checks port-settings-header port-settings-template ksa-metal-entries)

# port_folder_copy_as(<destination file> <source>): one file under another name
function(port_folder_copy_as destination source)
    get_filename_component(folder "${destination}" DIRECTORY)
    install(CODE "file(MAKE_DIRECTORY \"\${CMAKE_INSTALL_PREFIX}/${folder}\")
        file(COPY_FILE \"${source}\" \"\${CMAKE_INSTALL_PREFIX}/${destination}\")")
endfunction()
set(folder "${PROJECT_SOURCE_DIR}/src/player/port-folder")
set(res "${PORT_APP}/Resources")
set(licenses "${res}/Licenses")

# The commands, what they share, and the porter they run, with its shader edits
port_folder_copy("." "${folder}/port.sh" "${folder}/starmap.sh" "${folder}/benchmark.sh" "${folder}/README.md"
    "${PROJECT_SOURCE_DIR}/LICENSE" "${PROJECT_SOURCE_DIR}/THIRD_PARTY_NOTICES.md" "${stage}/VERSION")
port_folder_copy("lib" "${folder}/lib/common.sh" "${folder}/lib/port.env" "${stage}/lib/brutal.txt")
port_folder_copy("lib/porter" "${bin}/KsaPort.Porter/")

# The app without the game: Microsoft's .NET runtime as published (its signatures stay), the settings table and its
# template, the port's version for the Mac Port tab's title, FMOD, the icon
port_folder_copy("${PORT_APP}/MacOS" "${dotnet_runtime_SOURCE_DIR}/")
port_folder_copy("${res}" "${settings_table}" "${KSA_GENERATED_DIR}/port-settings.toml" "${stage}/VERSION"
    "${PROJECT_SOURCE_DIR}/third_party/fmod/libfmod.dylib" "${icon}/AppIcon.icns")
# KSA Metal Hooks, which the launcher loads, with the shared library and Harmony
port_folder_copy("${res}/Hooks" "${bin}/KsaMetal.Hooks.Loader/KsaMetal.Hooks.Loader.dll"
    "${bin}/KsaMetal.Hooks/KsaMetal.Hooks.dll" "${bin}/KsaPort.Common/KsaPort.Common.dll" "${harmony}")
# The benchmark: its director, which the hooks run, the program benchmark.sh runs, the tests and their vehicles
set(cli "${bin}/KsaMetal.Benchmark.Cli")
port_folder_copy("${res}/Benchmark" "${bin}/KsaMetal.Benchmark/KsaMetal.Benchmark.dll"
    "${cli}/KsaMetal.Benchmark.Cli.dll" "${cli}/KsaPort.Common.dll" "${cli}/KsaMetal.Benchmark.Cli.deps.json"
    "${cli}/KsaMetal.Benchmark.Cli.runtimeconfig.json" "${PROJECT_SOURCE_DIR}/src/game/benchmark/lineup/benchmark.txt")
# The vehicles' folders, without what the checkout keeps hidden in them
install(CODE "file(COPY \"${PROJECT_SOURCE_DIR}/src/game/benchmark/lineup/vehicles\"
    DESTINATION \"\${CMAKE_INSTALL_PREFIX}/${res}/Benchmark\" PATTERN \".*\" EXCLUDE)")

# Every component's licence and notices (THIRD_PARTY_NOTICES.md lists them); those of the sources compiled into the
# libraries and BRUTAL's are installed with them (CMakeLists.txt)
port_folder_copy("${licenses}" "${PROJECT_SOURCE_DIR}/THIRD_PARTY_NOTICES.md")
port_folder_copy("${licenses}/ksa-macos-port" "${PROJECT_SOURCE_DIR}/LICENSE")
port_folder_copy("${licenses}/fmod" "${PROJECT_SOURCE_DIR}/third_party/fmod/LICENSE.TXT"
    "${PROJECT_SOURCE_DIR}/third_party/fmod/NOTICE.txt")
port_folder_copy("${licenses}/dotnet" "${dotnet_runtime_SOURCE_DIR}/LICENSE.txt"
    "${dotnet_runtime_SOURCE_DIR}/ThirdPartyNotices.txt")
port_folder_copy("${licenses}/ksa-icon" "${icon}/NOTICE.txt")
port_folder_copy_as("${licenses}/starmap/LICENSE.txt" "${starmap_license_SOURCE_DIR}/LICENSE.txt")
port_folder_copy_as("${licenses}/harmony/LICENSE.txt" "${harmony_license_SOURCE_DIR}/LICENSE")

# StarMap, which starmap.sh adds to an app: its release is its Windows build, of which only two files are
# Windows-specific: StarMap.exe, a launcher the app doesn't need (the launcher runs StarMap.dll with the app's .NET
# runtime), left out, and StarMap.dll's AMD64 mark, changed to ARM64 like KSA.dll's (the porter's mark-arm64). The rest
# is platform-neutral .NET.
install(CODE "
    set(starmap \"\${CMAKE_INSTALL_PREFIX}/payload/starmap\")
    file(COPY \"${starmap_SOURCE_DIR}/\" DESTINATION \"\${starmap}\" PATTERN StarMap.exe EXCLUDE)
    file(GLOB assemblies \"\${starmap}/*.dll\")
    execute_process(COMMAND \"${DOTNET}\" \"${bin}/KsaPort.Porter/KsaPort.Porter.dll\" mark-arm64 \${assemblies}
        COMMAND_ERROR_IS_FATAL ANY)
    file(WRITE \"\${starmap}/version.txt\" \"${STARMAP_VERSION}\\n\")
")

# The .NET projects' tests, which need no game
add_test(NAME porter-tests
    COMMAND "${DOTNET}" "${bin}/KsaPort.Porter.Tests/KsaPort.Porter.Tests.dll" "${PROJECT_SOURCE_DIR}")
add_test(NAME common-tests
    COMMAND "${DOTNET}" "${bin}/KsaPort.Common.Tests/KsaPort.Common.Tests.dll" "${PROJECT_SOURCE_DIR}")
add_test(NAME benchmark-tests
    COMMAND "${DOTNET}" "${bin}/KsaMetal.Benchmark.Cli.Tests/KsaMetal.Benchmark.Cli.Tests.dll")
# The port folder's, which need `cmake --install` first (label port-folder): its contents audited, and benchmark.sh
# against a stand-in game
add_test(NAME port-folder-check COMMAND bash "${PROJECT_SOURCE_DIR}/scripts/check-port-folder.sh" "${PORT_FOLDER}")
add_test(NAME benchmark-script-tests
    COMMAND bash "${PROJECT_SOURCE_DIR}/src/player/benchmark-cli/tests/command.sh" "${PORT_FOLDER}")
set_tests_properties(port-folder-check benchmark-script-tests PROPERTIES LABELS port-folder)
