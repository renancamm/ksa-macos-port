# KSA Metal's tests (README.md in this folder) -> ksa-metal-tests in the build tree, linked with the driver's objects.
# ctest runs the smoke test; the shader corpus test runs on a corpus recorded with KSA_MTL_SHADER_CORPUS (label
# corpus, left out by build.sh).
add_executable(ksa-metal-tests tests.mm)
target_link_libraries(ksa-metal-tests PRIVATE ksa-metal-objects)
target_link_options(ksa-metal-tests PRIVATE -fobjc-arc)

add_test(NAME driver-smoke COMMAND ksa-metal-tests smoke)
set(KSA_MTL_CORPUS_DIR "$ENV{HOME}/Library/Caches/ksa-metal/corpus" CACHE PATH "The shader corpus the corpus test runs on")
add_test(NAME shader-corpus COMMAND ksa-metal-tests corpus "${KSA_MTL_CORPUS_DIR}")
set_tests_properties(shader-corpus PROPERTIES LABELS corpus)
