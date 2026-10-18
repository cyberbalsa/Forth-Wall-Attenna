# Python examples

The `forthwall` library handles authentication, Protobuf, hub selection, caching,
source backups, and command results. These examples run outside Space Engineers;
the game only handles bounded API requests from the plugin.

## Start here

From the repository root:

```bash
bash scripts/bootstrap-python.sh
.tools/venv/bin/python examples/python/getting_started.py
scripts/forthwall-dashboard
```

The bootstrap creates `.tools/venv` and installs the local Python package. It
doesn't change Steam or install/restart the game. Launch the configured game,
load a local world, and power an owned Forth Wall Attenna Hub to get live data.
The library finds the local Proton token file automatically. Use `--token-file
PATH` or `FORTHWALL_TOKEN_FILE` for another installation; keep token contents out
of code and command arguments.

For your own virtual environment, run `python -m pip install -e client/python`.
Python 3.11 or newer is required. No extra UI packages are needed for the terminal
dashboard on CachyOS/Linux.

## Status dashboard

```bash
scripts/forthwall-dashboard                         # interactive terminal
scripts/forthwall-dashboard --hub HUB_ID            # select among multiple hubs
scripts/forthwall-dashboard --once                  # one plain-text sample
scripts/forthwall-dashboard --json                  # one machine-readable sample
scripts/forthwall-dashboard --interval 3 --budget 4 --discovery-interval 15
```

Use **Up/Down** or **j/k** to select a block, **Page Up/Down** to move further,
**r** to refresh discovery, and **q** to quit. The selected row shows its entity
ID, latest PB output or compiler diagnostic, or controller waypoint and dampeners.
Terminal resize is supported; a window of at least 65 columns by 15 rows works.

The dashboard is read-only. It never uploads or runs a program, starts autopilot,
or acquires grid control. The header reports the connection and selected hub.
Each row shows a sample age; `~` means cached/stale. Route loss retains the last
reading for up to 60 seconds and marks it unavailable. A gateway outage triggers
reconnect delays of 2, 4, 8, 16, then 30 seconds. World changes discard old rows.
PB names and Echo output are sanitized before terminal rendering.

By default discovery runs every 10 seconds, with at most four status reads every
two seconds and 256 retained rows. Increase `--budget` (maximum 32) or
`--max-blocks` (maximum 512) if needed. Large fleets take multiple cycles to sample;
`--once` uses the same budget, so remaining rows initially say WAITING. Without a
terminal, output defaults to a single plain-text snapshot. One-shot mode exits 1
if the gateway is unavailable, and 0 after a connected sample.

## Library quick start

```python
from forthwall import Gateway

with Gateway() as game:                  # or Gateway(hub_id=123)
    print(game.hub().name)
    for block in game.programs():
        status = game.status(block.entity_id)
        print(block.name, status.initialized, status.output)

    for block in game.controllers():
        status = game.control_status(block.entity_id)
        print(block.name, status.speed, status.autopilot_enabled)
```

`Gateway` automatically chooses a hub when exactly one is working. Otherwise
call `hubs()` to list choices and supply `hub_id` when constructing the gateway.
Use a single gateway from one thread; independent threads need independent
connections. The lower-level `Client` and generated protocol remain available.

| Method | Result |
| --- | --- |
| `hubs()` / `hub()` | Hub metadata with `entity_id`, `name`, `grid_name`, `working` |
| `programs()` / `controllers()` | Reachable owned block metadata |
| `status(id)` | PB compilation, initialization, output, diagnostics, source hash |
| `control_status(id)` | Speed, autopilot, dampeners, current waypoint, link-loss flag |
| `read_program(id)` | `source` and `sha256` |
| `deploy(id, source)` / `deploy_file(id, path)` | `Deployment(status, backup_path)` |
| `run(id, argument="")` | PB status after explicit Main invocation |
| `set_waypoints(id, points, ...)` | Controller status; autopilot left off |
| `autopilot(id, enabled, ...)` | Controller status after the command |

Returned block/status objects expose the protocol fields as Python attributes;
callers don't need to construct Protobuf requests. Discovery has a five-second
TTL, status a one-second TTL, and the cache holds at most 256 entries by default.
Use `refresh=True` on a read, or `clear_cache()` to force new samples. Returned
values are copies, so editing them does not corrupt the cache. `cache_info`
reports hits, misses, and entry counts. Source reads are always fresh. Configure
`discovery_ttl`, `status_ttl`, and `cache_size` on `Gateway` if needed.

## Deploy and run deliberately

Replace the example IDs with discovered entity IDs. The following changes a PB:

```python
from forthwall import Gateway, GatewayError

pb_id = 123  # replace with an ordinary PB's entity ID
with Gateway(timeout_ms=10000) as game:
    result = game.deploy_file(pb_id, "examples/hello.cs")
    print("Previous program saved to", result.backup_path)
    print("Compiled:", result.status.compiled)
    status = game.run(pb_id, "hello from Python")
    print(status.output)
```

Deployment first reads and backs up the old source under
`~/.local/state/forthwall/backups/`, then sends its SHA-256 as a concurrency guard.
Set `backup_dir` to choose another directory. For an edit based on an earlier
read, pass that read's `sha256` as `expected_sha256` to `deploy`; a concurrent edit
then raises `GatewayError` with `SOURCE_CONFLICT`. Compiling doesn't invoke Main
unless `run=True`, though constructors can schedule their own updates.

On a `GatewayError`, inspect `error.response.error` and
`error.response.mutation_outcome`. A timeout/disconnect may leave a write applied;
check source/status before choosing your next action. Mutations never retry
automatically. A compile failure still leaves the source backup on disk.

## Waypoints and link-loss flags

```python
from forthwall import Gateway, Waypoint

rc_id = 456  # replace with a Remote Control block's entity ID
with Gateway() as game:
    game.set_waypoints(rc_id, [Waypoint(name="Home", x=100, y=200, z=-500)],
                       speed_limit=10, flight_pattern="one-way")
    game.autopilot(rc_id, True, on_link_loss="stop")
    # Use on_link_loss="continue" to keep flying after the antenna link drops.
    # game.autopilot(rc_id, False)  # stop and turn dampeners on
```

Waypoint coordinates are world metres. Route upload leaves autopilot off. Flight
patterns are `one-way`, `patrol`, and `circle`; collision avoidance defaults on.
The link-loss policy concerns antennas, not this Python connection, and must be
re-established after loading a save. Live steering and PB RPC are not implemented.

## Build another monitor

`StatusMonitor` supplies the dashboard's bounded reads and JSON-friendly snapshots:

```python
from forthwall import Gateway, StatusMonitor
import time

monitor = StatusMonitor(Gateway(), status_budget=4, discovery_interval=10)
try:
    while True:
        snapshot = monitor.poll()
        print(snapshot["connected"], snapshot["rows"])
        time.sleep(2)
finally:
    monitor.close()
```

Keep `poll()`, `refresh()`, and `close()` on the same thread. The example dashboard
uses a worker thread and transfers completed snapshots to its terminal UI, so
network waits don't block scrolling or quitting.
