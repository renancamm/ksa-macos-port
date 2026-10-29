# The GLFW shim: glfw_shim.c -> libGlfwShim.dylib in KSA_NATIVES_DIR
#
# The launcher loads the shim with DYLD_INSERT_LIBRARIES. It replaces a few GLFW and CoreGraphics calls to fix
# resolution lists, Fullscreen resolution, window positions and raw mouse errors (see the comment at the top of
# glfw_shim.c). It links RocketWerkz's GLFW from the Ahwoo.BRUTAL.Glfw.Native package, with the headers of the fork commit
# that library is built from.
set(CMAKE_C_FLAGS_RELEASE "")

add_library(brutal-glfw SHARED IMPORTED)
set_target_properties(brutal-glfw PROPERTIES
    IMPORTED_LOCATION "${KSA_NATIVES_DIR}/libglfw.3.dylib"
    INTERFACE_INCLUDE_DIRECTORIES "${glfw_headers_SOURCE_DIR}/include")

add_library(glfw-shim SHARED glfw_shim.c)
set_target_properties(glfw-shim PROPERTIES
    OUTPUT_NAME GlfwShim
    LIBRARY_OUTPUT_DIRECTORY "${KSA_NATIVES_DIR}"
    INSTALL_NAME_DIR @loader_path
    BUILD_WITH_INSTALL_NAME_DIR ON
    INSTALL_RPATH @loader_path
    BUILD_WITH_INSTALL_RPATH ON)
target_compile_options(glfw-shim PRIVATE -O2 -Wall -Werror)
target_compile_definitions(glfw-shim PRIVATE NDEBUG)
target_link_libraries(glfw-shim PRIVATE brutal-glfw "-framework CoreGraphics" "-framework CoreFoundation")

add_custom_command(TARGET glfw-shim POST_BUILD COMMAND codesign -f -s - "$<TARGET_FILE:glfw-shim>" 2> /dev/null)
