# BRUTAL's native libraries -> KSA_NATIVES_DIR, included by CMakeLists.txt once the packages of BRUTAL_PACKAGES
# (cmake/dependencies.cmake) are declared: RocketWerkz's macOS builds, unmodified, from each package's
# runtimes/osx-arm64/native/, with BRUTAL's licence, notice and the packages' third-party notices. The one file there
# that is not a library, Brutal.Monitor.Subprocess, is a program: it gets back the execute permission the package's
# zip doesn't keep.
# Some packages ship one library under several names, as identical copies (libglfw.dylib, libglfw.3.dylib,
# libglfw.3.5.dylib): the copies become links to the one named by the library's install name, so that the game and
# the GLFW shim, which links that name, load the same GLFW.
# The folder starts anew when the list of packages changes, so no library of an older package stays behind.
# Each package's Linux libraries (runtimes/linux-x64/native/, .so files) go by their SHA-256 into BRUTAL_LINUX_<name>
# for lib/brutal.txt (cmake/port-folder.cmake): the port's program compares them with the game's (Conversion.cs).
if(NOT "${BRUTAL_PACKAGES}" STREQUAL "$CACHE{KSA_BRUTAL_STAGED}")
    file(REMOVE_RECURSE "${KSA_NATIVES_DIR}")
endif()
file(MAKE_DIRECTORY "${KSA_NATIVES_DIR}/licenses/brutal")
# What the port folder gets, by name in KSA_NATIVES_DIR
set(brutal_staged "")
foreach(entry IN LISTS BRUTAL_PACKAGES)
    string(REGEX MATCHALL "[^ ]+" fields "${entry}")
    list(GET fields 0 id)
    list(GET fields 1 version)
    string(TOLOWER "${id}" lower)
    string(REPLACE "." "_" name "${lower}")
    FetchContent_MakeAvailable(${name})
    set(unpacked "${CMAKE_BINARY_DIR}/brutal/${name}")
    file(REMOVE_RECURSE "${unpacked}")
    # By its name: the download folder keeps the package of an earlier version when the version changes
    set(package "${${name}_SOURCE_DIR}/${lower}.${version}.nupkg")
    file(ARCHIVE_EXTRACT INPUT "${package}" DESTINATION "${unpacked}"
        PATTERNS "runtimes/osx-arm64/native/*" "runtimes/linux-x64/native/*" "*.txt") # every pattern must match: not
                                                                                     # all packages have LICENSES/
    file(GLOB linux LIST_DIRECTORIES false "${unpacked}/runtimes/linux-x64/native/*.so*")
    set(BRUTAL_LINUX_${name} "")
    foreach(library IN LISTS linux)
        get_filename_component(library_name "${library}" NAME)
        file(SHA256 "${library}" library_hash)
        string(APPEND BRUTAL_LINUX_${name} " ${library_name}=${library_hash}")
    endforeach()
    file(GLOB natives LIST_DIRECTORIES false "${unpacked}/runtimes/osx-arm64/native/*")
    if(NOT natives)
        message(FATAL_ERROR "${id} has no macOS libraries.")
    endif()
    set(programs ${natives})
    list(FILTER natives INCLUDE REGEX "\\.dylib$")
    list(FILTER programs EXCLUDE REGEX "\\.dylib$")
    if(natives)
        file(COPY ${natives} DESTINATION "${KSA_NATIVES_DIR}")
    endif()
    if(programs)
        file(COPY ${programs} DESTINATION "${KSA_NATIVES_DIR}" FILE_PERMISSIONS
            OWNER_READ OWNER_WRITE OWNER_EXECUTE GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)
    endif()
    foreach(staged IN LISTS natives programs)
        get_filename_component(staged "${staged}" NAME)
        list(APPEND brutal_staged "${KSA_NATIVES_DIR}/${staged}")
    endforeach()
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
# In the app next to the game, which finds them there
port_folder_copy("${PORT_APP}/Resources" ${brutal_staged})
