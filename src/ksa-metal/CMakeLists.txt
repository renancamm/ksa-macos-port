# KSA Metal (README.md in this folder) -> libvulkan.1.dylib (+ libvulkan.dylib) in KSA_NATIVES_DIR
#
# It implements the Vulkan calls KSA makes on top of Metal and takes the place of the Vulkan loader: Brutal, VulkanEx
# and GLFW find their Vulkan functions in it, and VMA gets them from Brutal. SPIRV-Cross and SPIRV-Tools are linked in
# statically; only the Vulkan functions and KSA Metal's own ksaMtl* calls are exported (exports.txt).

# Its own flags instead of CMake's Release ones (-O3 -DNDEBUG). KSA_MTL_OPT changes the optimization level.
set(CMAKE_CXX_FLAGS_RELEASE "")
set(CMAKE_OBJCXX_FLAGS_RELEASE "")
set(KSA_MTL_OPT "-O2" CACHE STRING "Optimization flag for KSA Metal")
# -g leaves a debug map in the library: the paths of the object files in the build tree, where lldb finds the debug
# info. It is of use only on the Mac that built it, so releases leave it out (build.sh --version), along with the
# build folder's path. The symbols stay, for crash reports.
set(KSA_MTL_DEBUG_MAP ON CACHE BOOL "Keep the debug map (object file paths) in KSA Metal")

# The driver's code as objects, which the library and the tests (tests/) both link
add_library(ksa-metal-objects OBJECT
    setup/config.mm setup/entry.mm setup/instance.mm setup/device.mm
    resources/memory.mm resources/images.mm resources/queries.mm resources/format.mm resources/descriptor.mm resources/accel.mm
    commands/encoders.mm commands/state.mm commands/draws.mm commands/passes.mm commands/transfers.mm commands/internal.mm
    pipelines/stages.mm pipelines/pipelines.mm pipelines/rtpipeline.mm
    shaders/translate.mm shaders/shadercache.mm shaders/corpus.mm shaders/tessspirv.cpp shaders/rtspirv.cpp
    presentation/swapchain.mm presentation/upscale.mm
    diagnostics/instruments.mm diagnostics/passdump.mm diagnostics/profile.mm)
set_target_properties(ksa-metal-objects PROPERTIES
    CXX_STANDARD 17
    CXX_EXTENSIONS OFF
    CXX_VISIBILITY_PRESET hidden
    OBJCXX_VISIBILITY_PRESET hidden)
target_compile_options(ksa-metal-objects PUBLIC ${KSA_MTL_OPT} -g -Wall -Werror $<$<COMPILE_LANGUAGE:OBJCXX>:-fobjc-arc>)
# Its headers by their path from here ("kmtl.h", "shaders/translate.h"), for its own files and the tests
target_include_directories(ksa-metal-objects PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_features(ksa-metal-objects PUBLIC cxx_std_17)
target_link_libraries(ksa-metal-objects PUBLIC
    port-settings Vulkan::Headers spirv-cross-msl spirv-cross-glsl spirv-cross-core SPIRV-Tools-link SPIRV-Tools-opt SPIRV-Tools-static
    "-framework Metal" "-framework MetalFX" "-framework QuartzCore" "-framework Foundation" "-framework CoreGraphics" "-framework ImageIO"
    "-framework UniformTypeIdentifiers" "-framework AppKit" "-framework CoreImage")

add_library(ksa-metal SHARED)
set_target_properties(ksa-metal PROPERTIES
    OUTPUT_NAME vulkan
    SOVERSION 1
    LIBRARY_OUTPUT_DIRECTORY "${KSA_NATIVES_DIR}"
    INSTALL_NAME_DIR @loader_path
    BUILD_WITH_INSTALL_NAME_DIR ON)
target_link_libraries(ksa-metal PRIVATE ksa-metal-objects)
target_link_options(ksa-metal PRIVATE -fobjc-arc "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports.txt")
set_property(TARGET ksa-metal APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/exports.txt")
if(NOT KSA_MTL_DEBUG_MAP)
    target_link_options(ksa-metal PRIVATE LINKER:-S)
endif()

# An ad-hoc signature of its own, as for everything built here (macOS kills a process that loads a library with an
# invalid one)
add_custom_command(TARGET ksa-metal POST_BUILD COMMAND codesign -f -s - "$<TARGET_FILE:ksa-metal>" 2> /dev/null)

add_subdirectory(tests)
