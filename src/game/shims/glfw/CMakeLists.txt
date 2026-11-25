# The GLFW shim: glfw_shim.c and cover.m -> libGlfwShim.dylib in KSA_NATIVES_DIR
#
# The launcher loads the shim with DYLD_INSERT_LIBRARIES. It replaces a few GLFW and CoreGraphics calls to fix
# resolution lists, Fullscreen resolution, window positions, raw mouse errors and display sleep, and places a window that
# covers its screen (cover.m: the menu bar and Dock over Borderless, the camera notch; see the comment at the top of
# glfw_shim.c). It links RocketWerkz's GLFW from the Ahwoo.BRUTAL.Glfw.Native package, with the headers of the fork commit
# that library is built from.
set(CMAKE_C_FLAGS_RELEASE "")
set(CMAKE_OBJC_FLAGS_RELEASE "")

add_library(brutal-glfw SHARED IMPORTED)
set_target_properties(brutal-glfw PROPERTIES
    IMPORTED_LOCATION "${KSA_NATIVES_DIR}/libglfw.3.dylib"
    INTERFACE_INCLUDE_DIRECTORIES "${glfw_headers_SOURCE_DIR}/include")

add_library(glfw-shim SHARED glfw_shim.c cover.m)
set_target_properties(glfw-shim PROPERTIES
    OUTPUT_NAME GlfwShim
    LIBRARY_OUTPUT_DIRECTORY "${KSA_NATIVES_DIR}"
    INSTALL_NAME_DIR @loader_path
    BUILD_WITH_INSTALL_NAME_DIR ON
    INSTALL_RPATH @loader_path
    BUILD_WITH_INSTALL_RPATH ON)
target_compile_options(glfw-shim PRIVATE -O2 -Wall -Werror $<$<COMPILE_LANGUAGE:OBJC>:-fobjc-arc>)
target_compile_definitions(glfw-shim PRIVATE NDEBUG)
target_link_libraries(glfw-shim PRIVATE brutal-glfw "-framework AppKit" "-framework CoreGraphics" "-framework CoreFoundation")

add_custom_command(TARGET glfw-shim POST_BUILD COMMAND codesign -f -s - "$<TARGET_FILE:glfw-shim>" 2> /dev/null)

# In the app, next to the game
port_folder_copy("${PORT_APP}/Resources" "$<TARGET_FILE:glfw-shim>")
