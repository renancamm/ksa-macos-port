# KSA Metal's tests

Tests of the driver that run without the game. `ksa-metal-tests` is linked with the driver's own objects, so it
calls the same code the library runs, including functions the library doesn't export. CMake builds it with the
driver, into `deps/build/cmake/src/game/driver/tests/`.

| Test | What it checks | Runs |
|---|---|---|
| `driver-smoke` | an instance and a device on the Mac's GPU, buffers in host-visible memory, a fill and a copy in a command buffer, a submit, a fence, the copy read back | after every build (`build.sh`) |
| `shader-corpus` | every translation of a recorded run again: its key from the recorded inputs, its MSL against the recorded one, Metal's compile of it | by hand, on a recorded corpus |

```bash
ctest --test-dir deps/build/cmake -LE corpus    # the smoke test
ctest --test-dir deps/build/cmake -L corpus     # the shader corpus test
```

## The shader corpus

A corpus is recorded by the driver while the game runs: with `KSA_MTL_SHADER_CORPUS=<dir>` (the port settings'
`shader_corpus`), each translation's inputs and result go to the folder once, translated by that build (the shader
cache is left out). A benchmark run records the shaders of the places it plays, and more runs into the same folder add
to it:

```bash
dist/ksa-macos-port/benchmark.sh mixed --fast --defaults --port shader_corpus=$HOME/Library/Caches/ksa-metal/corpus
dist/ksa-macos-port/benchmark.sh tour --fast --defaults --port shader_corpus=$HOME/Library/Caches/ksa-metal/corpus
```

`~/Library/Caches/ksa-metal/corpus` is where the test looks (`-DKSA_MTL_CORPUS_DIR=<dir>` changes it). The corpus
holds the game's shaders, so it stays on the Mac that recorded it and never goes into the repository.

| File | Holds |
|---|---|
| `<module hash>.spv` | a shader module, as the game created it |
| `<translation key>.in` | the rest of the inputs: entry point, stage, options, set layouts, specialization data, and the settings that shape a translation (`shader_constant_memory`, `skip_shadows_on_unlit_sides`, `spec`) |
| `<translation key>.out` | the result, as the shader cache stores it |

The test recomputes each translation's key from its `.in` file first: a different key means the record leaves out an
input, or the translator's version (`kTranslatorVersion` in `translate.mm`) changed, and the corpus needs recording
again. A translation recorded with other settings than the test's is skipped. A changed translation fails the test and
goes, before and now, to `$TMPDIR/ksa-metal-corpus/` as `<key>.recorded.txt` and `<key>.now.txt`: the entry point and
flags, then the MSL (`ksa-metal-tests corpus --out <dir> <corpus>` picks the folder). Once the change is the intended
one, `ksa-metal-tests corpus --update <corpus>` records the new results.
