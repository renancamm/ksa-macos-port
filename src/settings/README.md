# The port settings

One table, [`port-settings.txt`](port-settings.txt), holds every port setting: its key, type and default, the
variable it sets, what it needs, when it applies, the hook that does it, and its row in the Mac Port tab with its help.
The table's header explains its columns. Everything else that knows about a setting reads the table or is made from
it:

| Reader | How |
|---|---|
| the hooks' loader ([`../hooks/loader/`](../hooks/loader/)) | reads the table in the app when the game starts and sets each setting `port-settings.toml` changes as its variable |
| the Mac Port tab ([`../hooks/tab/`](../hooks/tab/)) | draws its rows from the table in the app |
| the hooks ([`../hooks/settings/Settings.cs`](../hooks/settings/Settings.cs)) | read their settings by key, against the table |
| `port-settings.toml`'s template | made from the table by `KsaPortSettings template`, into the app |
| the native code: KSA Metal ([`../ksa-metal/config.mm`](../ksa-metal/config.mm)), the KTX shim | read each variable and its default from [`port-settings.h`](port-settings.h), made from the table |

## The generator

`KsaPortSettings` ([`Generate.cs`](Generate.cs)) is a build tool, not part of the app. It reads the table with the
hooks' own reader ([`PortSettingsTable.cs`](../hooks/settings/PortSettingsTable.cs)). `build.sh` builds and runs it
first ([`scripts/build-settings.sh`](../../scripts/build-settings.sh)): it writes `port-settings.h` again when the table
changed (commit the two together), then checks that the READMEs keep up with the table, and stops the build when they
don't:

- every variable of the table is named in a README; one the driver, the KTX shim or the hooks read in their own
  README;
- every `KSA_*` or `MTL_*` variable a README names is in the table or read by some code (or is one of Apple's that
  Metal reads).

A release only checks: `port-settings.h` must be what the table makes.

| Command | Does |
|---|---|
| `KsaPortSettings template <table>` | writes `port-settings.toml`'s template to standard output |
| `KsaPortSettings generate <repository>` | writes `port-settings.h` again when it differs, then the checks |
| `KsaPortSettings check <repository>` | the checks, with `port-settings.h` as it is |

In `port-settings.h`, `PORT_SETTING_<KEY>` is a setting's variable and its default as the readers take them: a switch
`"<variable>", true` or `false` (`"<variable>", "<on value>"` for one whose on value is a word, such as
`KSA_MTL_SYNC=strict`), an `int` or `number` `"<variable>", <default>`, the other types `"<variable>"`. A setting's
default is then written once, in the table.

## Changing a setting

Edit its line in the table, then build: `./build.sh` makes the header and the template, and the hooks' tests check
the table's rules (values, needs, the hooks it names, the kinds the tab draws). A new row goes into the tab's presets
in the same change ([`../hooks/tab/Presets.cs`](../hooks/tab/Presets.cs)), and a new variable into the README of the
code that reads it.
