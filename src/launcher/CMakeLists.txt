# The launcher: launcher.c -> KSA in KSA_NATIVES_DIR, which scripts/make-kit.sh puts in the app as Contents/MacOS/KSA
#
# It sets up the game's environment and becomes .NET's dotnet running KSA (see the comment at the top of launcher.c).
set(CMAKE_C_FLAGS_RELEASE "")

add_executable(launcher launcher.c)
set_target_properties(launcher PROPERTIES
    OUTPUT_NAME KSA
    RUNTIME_OUTPUT_DIRECTORY "${KSA_NATIVES_DIR}")
target_compile_options(launcher PRIVATE -O2 -Wall -Werror)
target_compile_definitions(launcher PRIVATE NDEBUG)
target_link_libraries(launcher PRIVATE "-framework CoreFoundation")

# Signed with the app's identifier as its whole requirement: macOS keeps the player's answer to "access files in your
# Documents folder" for code that meets it, and an ad-hoc signature's own requirement is its hash, which every build
# changes, so the question would come back after every update of the port. Any program signed with the identifier
# meets it: a developer's certificate would hold more, and the port has none
add_custom_command(TARGET launcher POST_BUILD
    COMMAND codesign -f -s - -i "${BUNDLE_ID}" "-r=designated => identifier \"${BUNDLE_ID}\"" "$<TARGET_FILE:launcher>"
    VERBATIM)
