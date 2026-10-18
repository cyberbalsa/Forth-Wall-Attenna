# Using Forth Wall Attenna

Run these commands from the repository root after completing the
[installation](README.md#install). Keep your local world loaded and unpaused.
Commands use the hub's antenna network and only expose blocks owned by your player.

## Find your blocks

```bash
scripts/forthwall hubs
scripts/forthwall discover
scripts/forthwall controls
```

`hubs` lists gateway hubs. `discover` lists reachable Programmable Blocks, and
`controls` lists reachable Remote Control blocks. Responses include each block's
`entity_id`, name, grid, and working state.

The client selects a hub automatically when exactly one is working. With several
hubs, pass its ID before the command:

```bash
scripts/forthwall --hub HUB_ID discover
scripts/forthwall --hub HUB_ID controls
```

Replace `HUB_ID`, `PB_ID`, and `CONTROL_ID` in the examples with discovered IDs.
Ordinary PBs need no helper script or Custom Data flag. A relay station needs
powered antennas; it does not need another PB or hub.

The plugin checks the game's antenna graph in both directions. Radio, laser, and
mixed routes are chosen automatically, including relays. There is no extra
straight-line distance limit between the hub and target. Power, ownership, and
the working antenna route still govern every command.

## Watch status

```bash
scripts/forthwall-dashboard
scripts/forthwall-dashboard --hub HUB_ID
scripts/forthwall-dashboard --once
scripts/forthwall-dashboard --json
```

The dashboard is read-only. It shows PB compilation and output, controller speed
and autopilot state, and the age of each sample. Select a row with the arrow keys
or **j/k** to see its ID and details. Use **r** to refresh and **q** to quit.

Discovery refreshes every ten seconds. Each two-second polling cycle reads up to
four block statuses, rotating through the fleet. A `~` marks a stale reading.
Missing blocks remain visible with their last reading for up to sixty seconds;
world changes discard old rows. Connection failures trigger retries with a delay
of up to thirty seconds.

Tune the polling rate and row limit when needed:

```bash
scripts/forthwall-dashboard --interval 3 --budget 8 --discovery-interval 15 --max-blocks 256
```

`--budget` accepts 1–32 status reads per cycle; `--max-blocks` accepts 1–512 rows.
One-shot output uses the same budget, so a large fleet may initially include rows
waiting for their first sample. Without a terminal, the dashboard prints one
plain-text snapshot and exits.

The hub's main LCD shows operational state, reachable PB/controller counts, client
count, and the last command result. Its smaller LCD shows gateway and queue state.
The terminal's right panel adds the local endpoint and hub ID. Hub controls are
power, naming, visibility, and Refresh antenna status; programming and manual LCD
editors are hidden on hubs. Ordinary PBs retain their usual controls.

## Upload and run a script

Inspect a PB, download its source, then deploy the included example:

```bash
scripts/forthwall status PB_ID
scripts/forthwall read PB_ID --output downloaded.cs
scripts/forthwall deploy PB_ID examples/hello.cs
scripts/forthwall run PB_ID "hello from Python"
scripts/forthwall status PB_ID
```

`read` creates a new file and refuses to overwrite an existing one. Deployment
backs up the previous program under `~/.local/state/forthwall/backups/` and sends
its SHA-256 as a guard against concurrent edits. Choose another backup directory
with `deploy ... --backup-dir PATH`.

Uploading compiles and initializes the script. Main runs only when you invoke
`run` or include `--run` on deployment:

```bash
scripts/forthwall deploy PB_ID examples/hello.cs --run --argument hello
```

A constructor can schedule future script updates even without `--run`. The
game's compiler runs synchronously, so a large or cold compilation can briefly
stall the game. Source hashing, networking, and file operations run outside the
game thread.

CLI requests default to a ten-second deadline. For a slower compile:

```bash
scripts/forthwall --timeout-ms 30000 deploy PB_ID my-script.cs
```

Timeouts and disconnects never trigger an automatic retry of a write. If the
response says `MAY_HAVE_APPLIED`, read the source or status before deciding what
to send next. A failed compilation can still leave the new source on the PB;
the old source remains in the backup directory.

## Send a waypoint mission

The ship needs an owned, powered Remote Control block, working antennas, and the
flight hardware required to follow its route. Use a JSON file of world-space
coordinates in metres:

```json
[
  {"name": "Destination", "x": 1000, "y": 200, "z": -500}
]
```

Upload the route, then start autopilot explicitly:

```bash
scripts/forthwall control-status CONTROL_ID
scripts/forthwall waypoints CONTROL_ID my-waypoints.json --speed 10 --flight one-way
scripts/forthwall autopilot CONTROL_ID start --on-link-loss stop
scripts/forthwall control-status CONTROL_ID
scripts/forthwall autopilot CONTROL_ID stop
```

Route upload replaces existing waypoints and leaves autopilot off. A route can
contain up to 32 waypoints, with a speed limit above zero and at most 100 m/s.
Flight modes are `one-way`, `patrol`, and `circle`. Collision avoidance defaults
on; `--disable-collision-avoidance` and `--docking` are available on `waypoints`.

Choose the antenna link-loss behavior when starting the mission:

| Flag | When the antenna route disappears |
| --- | --- |
| `--on-link-loss stop` | Stop autopilot and turn dampeners on. |
| `--on-link-loss continue` | Keep following uploaded waypoints. This is the default. |

The policy concerns the in-game antenna link. Closing the Python client does not
stop an uploaded mission. Link-loss monitoring lasts for the current world session;
re-enable the stop policy after loading a save. Up to sixteen missions can use it
at once.

An explicit stop turns dampeners on unless you pass `--coast-on-stop`. Commands
that change a route or autopilot state reject a grid with a player at the controls.
Direct steering is not implemented yet.

## Write your own Python program

```python
from forthwall import Gateway

with Gateway() as game:
    for block in game.programs():
        status = game.status(block.entity_id)
        print(block.name, status.initialized, status.output)
```

Run the starter from the repository environment:

```bash
.tools/venv/bin/python examples/python/getting_started.py
```

`Gateway` handles authentication and Protobuf requests. It caches discovery for
five seconds and status for one second, with a 256-entry limit by default. Use
`refresh=True` to bypass a read's cache. Source reads are always fresh, and writes
invalidate cached data. Keep each gateway on one thread.

The [Python guide](examples/python/README.md) covers guarded deployment, waypoints,
autopilot flags, and `StatusMonitor`, the dashboard's reusable polling library.

## Files and connection settings

The default endpoint is `ws://127.0.0.1:18751/forthwall`. Override it with `--uri`
before a CLI command or as a dashboard option. The plugin accepts authenticated
loopback connections with the `forthwall.v1` subprotocol and binary Protobuf
messages. The schema is in [protocol/forthwall.proto](protocol/forthwall.proto).
Browser-origin connections are rejected; use the Python client for local tools.

Under the default Steam installation, plugin data lives in:

```text
~/.local/share/Steam/steamapps/compatdata/244850/pfx/drive_c/users/steamuser/
  AppData/Roaming/SpaceEngineers/
    Mods/ForthWallAttenna/
    Storage/ForthWallAttenna/gateway.xml
    Storage/ForthWallAttenna/gateway.token
    Storage/ForthWallAttenna/gateway.log
```

The client reads `gateway.token` directly. For another location, pass
`--token-file PATH` or set `FORTHWALL_TOKEN_FILE` to the file path. Keep the token
contents out of source code, command arguments, and bug reports.

To change the port, close the game, edit `Port` in `gateway.xml`, restart, and
connect with the matching `--uri`. The plugin remains bound to loopback.

Pulsar and plugin files stay in the checkout:

```text
.tools/pulsar/Legacy.exe
.tools/pulsar/Legacy/Local/ForthWall.Plugin/
.tools/pulsar/Legacy/Profiles/Current.xml
.tools/pulsar/Legacy/info.log
```

## Troubleshooting

| Result | Check |
| --- | --- |
| Connection refused | Launch through Steam's wrapper; check Pulsar's `info.log` and `gateway.log`. |
| `NO_WORLD` | Load a local single-player world with the mod enabled. |
| `TIMEOUT` | Unpause the world. If it was already running, check the logs for an adapter failure. |
| No working hub | Check hub ownership, completion, and power. With multiple hubs, supply `--hub`. |
| `UNREACHABLE` | Check the antenna route in both directions, including power and any laser connection. |
| `ACCESS_DENIED` | The hub and target must be owned by the local player. |
| `NOT_WORKING` | Power on and complete the relevant block. |
| `SOURCE_CONFLICT` | Read the current source before deploying your edit again. |
| `COMPILE_FAILED` | Inspect `status` diagnostics and correct the script. |
| `BUSY` | Release player control of the grid, or wait for the pending operation to finish. |

If a game update disables the adapter, rebuild against the installed assemblies
and check the log for the failing API type. Include game/plugin versions and the
error type when reporting a bug, without the token file or uploaded source.

## Test world

Create the disposable relay test save from your installed Empty World template:

```bash
.tools/venv/bin/python scripts/create-test-world.py
```

With multiple Steam accounts, add `--steam-user ACCOUNT_ID`. The script refuses
to overwrite an existing save named `Forth Wall Attenna - Relay Test`.

Load that world, leave it unpaused, and run:

```bash
.tools/venv/bin/python scripts/smoke-test.py
```

The hub and target ship are 4 km apart with 2.5 km radios and an antenna-only relay
between them. The smoke test deploys a temporary script, checks execution and both
link-loss policies, restores the radio and previous program, and stops autopilot.
Run it in that generated world; it changes the fixture's PB and controller state.
