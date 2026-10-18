# Forth Wall Attenna

Control owned Space Engineers 1 Programmable Blocks and Remote Control blocks
from Python through your in-game antenna network.

Build a Forth Wall Attenna Hub, connect it to powered antennas, and use the Python
client to discover blocks, upload scripts, run them, or send waypoint missions.
The hub uses the vanilla Programmable Block model with dedicated status LCDs and
gateway controls. A terminal dashboard shows block status outside the game.

This version targets local single-player on CachyOS with Proton. Radio-relay
discovery, script deployment, waypoint flight, both link-loss policies, and the
large-grid hub displays have passed live tests. Laser and mixed routes use the
game's antenna graph but still need live validation. Multiplayer, dedicated
servers, live steering, and PB RPC are not implemented.

[Usage guide](USAGE.md) · [Python examples and API](examples/python/README.md) ·
[Development notes](DEVELOPMENT.md)

## Install

You need an x86-64 Linux machine with Steam and Space Engineers 1 installed.
Launch the game through Proton once so it creates its user-data directory, then
close both the game and Steam before running the installer.

Install these build tools first:

- Git, Bash, `curl`, and `unzip`.
- Python 3.11 or newer, with `pip` and `venv`.
- The [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
- [`protoc` 36.1](https://github.com/protocolbuffers/protobuf/releases/tag/v36.1)
  on your `PATH`, matching the checked-in generated protocol code.

Clone the repo and point the build at your installed game:

```bash
git clone https://github.com/cyberbalsa/Forth-Wall-Attenna.git "Forth Wall Attenna"
cd "Forth Wall Attenna"

export SPACE_ENGINEERS_BIN64="$HOME/.local/share/Steam/steamapps/common/SpaceEngineers/Bin64"
bash scripts/setup-local.sh --set-launch-options
```

Change `SPACE_ENGINEERS_BIN64` if the game is in another Steam library. The build
reads game assemblies from that directory; it does not copy or distribute them.
If `dotnet` is outside your `PATH`, set `FORTHWALL_DOTNET` to its executable. The
build also checks `.tools/dotnet/dotnet` for a local SDK.

Setup downloads [Pulsar 2.4.3](https://github.com/SpaceGT/Pulsar/releases/tag/v2.4.3),
creates `.tools/venv`, builds and tests the plugin, and installs the local block
mod. It adds the plugin to Pulsar's Legacy profile and configures Steam to launch
the game through `scripts/launch-proton.sh`. Pulsar's updater, statistics, and
Discord integration are disabled by that wrapper.

The installer selects the Steam account automatically when it finds one account.
If it finds several, rerun with the numeric directory name under Steam's
`userdata/`:

```bash
bash scripts/setup-local.sh --set-launch-options --steam-user ACCOUNT_ID
```

The default Steam root is `~/.local/share/Steam`. Use `--steam-root PATH` for a
different root containing `userdata/` and `steamapps/compatdata/244850/`. Layouts
that split those directories across different roots need manual installation.

If you already have launch options, setup stops before changing them. Run setup
without `--set-launch-options`, then add the wrapper immediately before
`%command%` in Steam's launch options, preserving your existing environment
settings and game arguments. With no other options, the result is:

```text
"/absolute/path/to/Forth Wall Attenna/scripts/launch-proton.sh" %command%
```

Setup saves installation backups under `artifacts/install-backups/`. Keep the
checkout in place: Steam's launch option points to its wrapper and `.tools/pulsar`.

## Load a world

1. Start Space Engineers from Steam. Pulsar should load with it.
2. Enable Experimental mode in the game settings if needed, then create or edit a
   local world with in-game scripts enabled.
3. Add the local `ForthWallAttenna` mod to that world.
4. Build and power a Forth Wall Attenna Hub owned by your player. Connect it to
   the antenna network containing your owned PBs or Remote Control blocks.
5. Leave the world unpaused and run these commands from the checkout:

```bash
scripts/forthwall hubs
scripts/forthwall discover
scripts/forthwall-dashboard
```

The Python client reads the local authentication token file automatically. Hubs
with a working gateway show READY on their model LCDs. The dashboard lists
reachable blocks and their latest status; press **q** to quit or **r** to refresh.
Continue with [USAGE.md](USAGE.md) for deployment and grid-control commands.

## Python tools only

If the plugin is already installed, this sets up the client and examples without
building the plugin:

```bash
bash scripts/bootstrap-python.sh
.tools/venv/bin/python examples/python/getting_started.py
scripts/forthwall-dashboard
```

The installable `forthwall` package provides discovery, status, guarded script
deployment, execution, waypoints, and autopilot commands. The
[Python guide](examples/python/README.md) includes examples and cache settings.

## Update, test, or remove

Close the game before replacing the installed plugin. Steam can remain open when
launch options are unchanged:

```bash
git pull --ff-only
export SPACE_ENGINEERS_BIN64="$HOME/.local/share/Steam/steamapps/common/SpaceEngineers/Bin64"
bash scripts/bootstrap-python.sh
bash scripts/build.sh
.tools/venv/bin/python scripts/install-local.py
```

The build runs 14 core/transport tests and, when `.tools/venv` exists, 15 Python
client, SDK, monitor, and setup tests. These checks run separately from live game
tests. The plugin targets .NET Framework 4.8; the tests run on .NET 9.

For a disposable radio-relay test world, see [the test-world steps](USAGE.md#test-world).
Small-grid hub placement, survival availability, laser-only routes, mixed routes,
and alternate relay paths still need live checks. Game updates may require plugin
changes because PB compilation and reachability use internal game APIs.

To remove startup integration, delete this project's wrapper from Space Engineers'
Steam launch options. Previous options are recorded in the installation backup's
`manifest.json`; restore those options without replacing your whole Steam config.
Remove hub blocks and the local mod from any worlds you plan to keep using before
deleting `Mods/ForthWallAttenna`. The installed files and log locations are listed
in [USAGE.md](USAGE.md#files-and-connection-settings).
