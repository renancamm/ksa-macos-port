# The port settings

One table, [`port-settings.txt`](port-settings.txt), holds every port setting: its key, type and default, the
variable it sets, what it needs, when it applies, the hook that does it, and its row in the Mac Port tab with its help.
The table's header explains its columns. Everything else that knows about a setting reads the table or is made from
it.

| Path | What it holds |
|---|---|
| [`port-settings.txt`](port-settings.txt) | the table |
| [`PortSettings.cs`](PortSettings.cs), [`PortSetting.cs`](PortSetting.cs) | the library, `KsaPortSettings.dll`: the table, `port-settings.toml`, the needs, and the environment the game runs with |
| [`generator/`](generator/) | the generator: the file's template, `port-settings.h`, and the checks that keep the repository in step with the table |
| [`port-settings.h`](port-settings.h) | the variables and defaults the native code reads, made from the table |
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
values, or belongs to a setting whose needs aren't met means the default. The native code, KSA Metal
([`../ksa-metal/setup/config.mm`](../ksa-metal/setup/config.mm)) and the KTX shim, reads each variable and its
default from [`port-settings.h`](port-settings.h).

## The generator

`KsaPortSettingsGenerator` ([`generator/Generate.cs`](generator/Generate.cs)) is a build tool, not part of the app.
`build.sh` builds and runs it first ([`scripts/build-settings.sh`](../../scripts/build-settings.sh)): it writes
`port-settings.h` again when the table changed (commit the two together), then checks the repository against the
table, and stops the build when it isn't in step:

- every variable of the table is named in a README; one the driver, the KTX shim or the hooks read in their own
  README;
- every `KSA_*` or `MTL_*` variable a README names is in the table or read by some code (or is one of Apple's that
  Metal reads);
- every hook the table names is one `Hooks.Apply` runs, every key the hooks read is in the table, and every live
  setting with a variable is read by the hooks.

A release only checks: `port-settings.h` must be what the table makes.

| Command | Does |
|---|---|
| `KsaPortSettingsGenerator template <table>` | writes `port-settings.toml`'s template to standard output |
| `KsaPortSettingsGenerator generate <repository>` | writes `port-settings.h` again when it differs, then the checks |
| `KsaPortSettingsGenerator check <repository>` | the checks, with `port-settings.h` as it is |

In `port-settings.h`, `PORT_SETTING_<KEY>` is a setting's variable and its default as the readers take them: a switch
`"<variable>", true` or `false` (`"<variable>", "<on value>"` for one whose on value is a word, such as
`KSA_MTL_SYNC=strict`), an `int` or `number` `"<variable>", <default>`, the other types `"<variable>"`. A setting's
default is then written once, in the table.

## Changing a setting

Edit its line in the table, then build: `./build.sh` makes the header and the template, the generator checks the
repository, and the library's tests check the table's rules (values, needs, the kinds the tab draws). A new row goes
into the tab's presets in the same change ([`../hooks/tab/Presets.cs`](../hooks/tab/Presets.cs)), and a new variable
into the README of the code that reads it.
