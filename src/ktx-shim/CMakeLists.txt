# The KTX shim: ktx_shim.c -> libKtxShim.dylib in KSA_NATIVES_DIR
#
# The launcher loads the shim with DYLD_INSERT_LIBRARIES. It replaces libktx's create and destroy calls so that the
# pixels KSA keeps of each texture are mapped from a file instead of held in memory of their own (see the comment at
# the top of ktx_shim.c). It links RocketWerkz's libktx from the Ahwoo.BRUTAL.Ktx.Native package.
set(CMAKE_C_FLAGS_RELEASE "")

add_library(brutal-ktx SHARED IMPORTED)
set_target_properties(brutal-ktx PROPERTIES IMPORTED_LOCATION "${KSA_NATIVES_DIR}/libKtx.dylib")

add_library(ktx-shim SHARED ktx_shim.c)
set_target_properties(ktx-shim PROPERTIES
    OUTPUT_NAME KtxShim
    LIBRARY_OUTPUT_DIRECTORY "${KSA_NATIVES_DIR}"
    INSTALL_NAME_DIR @loader_path
    BUILD_WITH_INSTALL_NAME_DIR ON
    INSTALL_RPATH @loader_path
    BUILD_WITH_INSTALL_RPATH ON)
target_compile_options(ktx-shim PRIVATE -O2 -Wall -Werror)
target_compile_definitions(ktx-shim PRIVATE NDEBUG)
target_link_libraries(ktx-shim PRIVATE brutal-ktx)

add_custom_command(TARGET ktx-shim POST_BUILD COMMAND codesign -f -s - "$<TARGET_FILE:ktx-shim>" 2> /dev/null)
