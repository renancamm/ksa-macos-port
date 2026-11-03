# The port settings

One table, [`port-settings.txt`](port-settings.txt), holds every port setting: its key, type and default, what it
needs, when it applies, what does it (the driver, the KTX shim, a hook), its values in the Mac Port tab's presets,
and its row in the tab with its help. The table's header explains its columns. Everything else that knows about a
setting reads the table or is made from it.

A setting has one name, its key, and its environment variable is `KSA_MTL_` and the key in capitals:
`fused_tessellation` is `KSA_MTL_FUSED_TESSELLATION`. Only two settings set Apple's own variable instead, which Metal
reads: `metal_performance_hud` (`MTL_HUD_ENABLED`) and `frame_capture` (`MTL_CAPTURE_ENABLED`). A hook goes by its
class's name, in the table as in the log.

| Path | What it holds |
|---|---|
| [`port-settings.txt`](port-settings.txt) | the table |
| [`PortSettings.cs`](PortSettings.cs), [`PortSetting.cs`](PortSetting.cs) | the library, `KsaPortSettings.dll`: the table, `port-settings.toml`, the needs, and the environment the game runs with |
| [`generator/`](generator/) | the generator: the file's template, the native code's and the hooks' typed settings, and the checks that keep the repository in step with the table |
| [`port-settings.h`](port-settings.h) | the native code's settings, made from the table |
| [`tests/`](tests/) | the library's tests |

## The library

`KsaPortSettings.dll` is in the app's `Contents/Resources/Hooks/`, next to the hooks, and in its `Benchmark/`, next to
the report program. Who uses it:

| Reader | How |
|---|---|
| the hooks' loader ([`../hooks/loader/`](../hooks/loader/)) | reads the table and `port-settings.toml` when the game starts, and sets each setting the file changes as its variable (`PortSettings.Plan`) |
| the hooks ([`../hooks/Settings.cs`](../hooks/Settings.cs)) | read each setting back from its variable (`PortSettings.Load`) |
| the Mac Port tab ([`../hooks/tab/`](../hooks/tab/)) | draws its rows from the table, and writes the values it changes into the file (`PortSettings.Save`) |
| the benchmark's report program ([`../benchmark/report/`](../benchmark/report/)) | checks `--port` against the table and writes it into the run's file |

A variable already set when the game starts wins over the file. A variable that is unset, holds none of its setting's
values, or belongs to a setting whose needs aren't met means the default.

## Typed settings

The code reads a setting by a name made from its key, typed as the table says, so a setting that isn't in the table
doesn't compile. Two files are made from the table:

| File | Holds |
|---|---|
| [`port-settings.h`](port-settings.h) | `PortSettings`, a field per setting the driver or the KTX shim does (`fusedTessellation`, a `bool`), and `portSettingsRead()`, which reads them from their variables with the values the library takes (it doesn't apply needs to a variable set outside the game); KSA Metal keeps them in `kmtlConfig()` ([`../ksa-metal/setup/config.mm`](../ksa-metal/setup/config.mm)) |
| [`../hooks/Settings.Generated.cs`](../hooks/Settings.Generated.cs) | a property of the hooks' `Settings` per setting the hooks do (`Settings.TerrainCache`, a `bool`; `Settings.RenderScale`, a `double`): read once when the hooks load, or each time for a live setting |

## The generator

`KsaPortSettingsGenerator` ([`generator/Generate.cs`](generator/Generate.cs)) is a build tool, not part of the app.
`build.sh` builds and runs it first ([`scripts/build-settings.sh`](../../scripts/build-settings.sh)): it makes the
two typed files again when the table changed (commit them with it), then checks the repository against the table,
and stops the build when it isn't in step:

- each setting's `by` column is what the code does: the driver and the KTX shim use the field of each setting it
  names for them, the hooks the property, and the hook it names is one `Hooks.Apply` runs and reads it (in its
  class's file, in a class it applies, or where `Hooks.Apply` switches it);
- each setting is named by its key in a table row of the README of every component that reads it;
- the code names no `KSA_MTL_` variable the table doesn't have, and every `KSA_*` or `MTL_*` variable a README names
  is in the table or in some code (or is one of Apple's that Metal reads);
- a live setting is read by the hooks, since the tab's Apply only changes what they read.

A release only checks: the two files must be what the table makes.

| Command | Does |
|---|---|
| `KsaPortSettingsGenerator template <table>` | writes `port-settings.toml`'s template to standard output |
| `KsaPortSettingsGenerator generate <repository>` | writes the typed files again when they differ, then the checks |
| `KsaPortSettingsGenerator check <repository>` | the checks, with the typed files as they are |

## Changing a setting

Edit its line in the table, then build: `./build.sh` makes the typed files and the template, the generator checks the
repository, and the library's tests check the table's rules (values, needs, presets, the kinds the tab draws). A new
Extra Performance row gets its presets' values in the same line (on in Performance, the tests check), and its key goes
into the README of the code that reads it.
