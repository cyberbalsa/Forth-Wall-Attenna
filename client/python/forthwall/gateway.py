"""Small synchronous SDK: one connection, one selected hub, no mutation retries."""
from __future__ import annotations

from collections import OrderedDict
from dataclasses import dataclass
from datetime import datetime, timezone
import math
import os
from pathlib import Path
import time
from typing import Iterable

from .client import Client, GatewayError
from . import forthwall_pb2 as pb

Waypoint = pb.Waypoint


class HubSelectionError(ValueError):
    """Choose hub_id explicitly when there isn't exactly one working hub."""


@dataclass(frozen=True)
class CacheInfo:
    hits: int
    misses: int
    entries: int
    capacity: int


@dataclass(frozen=True)
class Deployment:
    status: pb.BlockStatus
    backup_path: Path


class Gateway:
    """Convenience API over Client. Use as a context manager in a single thread.

    Discovery is cached for five seconds and status for one second by default.
    Pass refresh=True to a read to bypass its cache. Source reads and mutations
    are never cached. Cached protobuf values are copied before returning them.
    Authentication is read from the local token file, never embedded in code.
    """

    def __init__(self, uri="ws://127.0.0.1:18751/forthwall", token_file=None,
                 hub_id: int | None = None, timeout_ms=3000, *,
                 discovery_ttl=5.0, status_ttl=1.0, cache_size=256):
        if not all(math.isfinite(v) and v >= 0 for v in (discovery_ttl, status_ttl)):
            raise ValueError("Cache lifetimes must be finite and nonnegative.")
        if not isinstance(cache_size, int) or not 1 <= cache_size <= 4096:
            raise ValueError("cache_size must be between 1 and 4096.")
        self.client = Client(uri, token_file, timeout_ms)
        self.requested_hub_id = hub_id
        self.discovery_ttl, self.status_ttl = discovery_ttl, status_ttl
        self._capacity = cache_size
        self._cache = OrderedDict()
        self._hits = self._misses = 0
        self._open = False

    def connect(self) -> Gateway:
        if not self._open:
            self.clear_cache()
            self.client.session_id = ""
            self.client.__enter__()
            self._open = True
        return self

    def close(self):
        self.client.__exit__()
        self._open = False
        self.client.session_id = ""
        self.clear_cache()

    def __enter__(self) -> Gateway:
        return self.connect()

    def __exit__(self, *_):
        self.close()

    @property
    def session_id(self) -> str:
        return self.client.session_id

    @property
    def cache_info(self) -> CacheInfo:
        return CacheInfo(self._hits, self._misses, len(self._cache), self._capacity)

    def clear_cache(self):
        self._cache.clear()

    def _call(self, operation, message, hub_id=0):
        if not self._open:
            raise RuntimeError("Open Gateway with a 'with' statement or connect() first.")
        previous_session = self.session_id
        try:
            response = self.client.request(operation, message, hub_id)
        except GatewayError as error:
            self.clear_cache()
            if error.response.error.code in (pb.Error.NO_WORLD, pb.Error.WRONG_SESSION, pb.Error.WORLD_UNLOADED):
                self.client.session_id = ""
            raise
        except Exception:
            self.close()
            raise
        if previous_session != self.session_id:
            self.clear_cache()
        return response

    def _read(self, operation, message, hub_id=0, *, ttl=0.0, refresh=False):
        if not self._open:
            raise RuntimeError("Open Gateway before reading cached data.")
        key = (self.session_id, hub_id, operation, message.SerializeToString())
        cached = self._cache.get(key)
        if not refresh and cached is not None and time.monotonic() < cached[0]:
            self._hits += 1
            self._cache.move_to_end(key)
            result = pb.Response()
            result.CopyFrom(cached[1])
            return result
        self._cache.pop(key, None)
        self._misses += 1
        response = self._call(operation, message, hub_id)
        if ttl > 0:
            # ListHubs may establish a new world generation during this call.
            key = (self.session_id, hub_id, operation, message.SerializeToString())
            copy = pb.Response()
            copy.CopyFrom(response)
            self._cache[key] = (time.monotonic() + ttl, copy)
            self._cache.move_to_end(key)
            while len(self._cache) > self._capacity:
                self._cache.popitem(last=False)
        return response

    def hubs(self, *, refresh=False) -> tuple[pb.Block, ...]:
        return tuple(self._read("list_hubs", pb.ListHubs(), ttl=self.discovery_ttl, refresh=refresh).blocks.blocks)

    def hub(self, *, refresh=False) -> pb.Block:
        hubs = self.hubs(refresh=refresh)
        if self.requested_hub_id is not None:
            for hub in hubs:
                if hub.entity_id == self.requested_hub_id:
                    return hub
            raise HubSelectionError(f"Hub {self.requested_hub_id} is not available in this world.")
        working = [hub for hub in hubs if hub.working]
        if len(working) != 1:
            ids = ", ".join(str(h.entity_id) for h in working) or "none"
            raise HubSelectionError(f"Choose hub_id (working hubs: {ids}).")
        return working[0]

    def programs(self, *, refresh=False) -> tuple[pb.Block, ...]:
        hub = self.hub()
        return tuple(self._read("discover", pb.Discover(), hub.entity_id,
                                ttl=self.discovery_ttl, refresh=refresh).blocks.blocks)

    def controllers(self, *, refresh=False) -> tuple[pb.Block, ...]:
        hub = self.hub()
        return tuple(self._read("discover_controls", pb.DiscoverControls(), hub.entity_id,
                                ttl=self.discovery_ttl, refresh=refresh).blocks.blocks)

    def status(self, block_id: int, *, refresh=False) -> pb.BlockStatus:
        return self._read("get_status", pb.GetStatus(block_id=block_id), self.hub().entity_id,
                          ttl=self.status_ttl, refresh=refresh).status

    def control_status(self, block_id: int, *, refresh=False) -> pb.ControlStatus:
        return self._read("get_control_status", pb.GetControlStatus(block_id=block_id), self.hub().entity_id,
                          ttl=self.status_ttl, refresh=refresh).control_status

    def read_program(self, block_id: int) -> pb.ProgramSource:
        return self._call("read_program", pb.ReadProgram(block_id=block_id), self.hub().entity_id).program

    def _mutate(self, operation, message):
        hub_id = self.hub().entity_id
        try:
            return self._call(operation, message, hub_id)
        finally:
            self.clear_cache()

    def deploy(self, block_id: int, source: str, *, run=False, argument="",
               expected_sha256: str | None = None, backup_dir=None) -> Deployment:
        """Back up fresh source, then compile with an optimistic SHA-256 guard.

        Main runs only with run=True. Script constructors can schedule updates.
        On error the backup remains on disk; inspect GatewayError.response for
        the mutation outcome before deciding what to do next.
        """
        if len(source.encode("utf-8")) > 256 * 1024:
            raise ValueError("Source exceeds 256 KiB.")
        if len(argument.encode("utf-8")) > 4096:
            raise ValueError("Argument exceeds 4096 bytes.")
        if expected_sha256 is not None and (len(expected_sha256) != 64 or any(c not in "0123456789abcdefABCDEF" for c in expected_sha256)):
            raise ValueError("expected_sha256 must be a SHA-256 hex digest.")
        # Pin the hub and session for the read/write pair; never auto-select a
        # different world or hub between reading the guard and sending the write.
        hub_id = self.hub().entity_id
        before = self._call("read_program", pb.ReadProgram(block_id=block_id), hub_id).program
        directory = Path(backup_dir) if backup_dir is not None else Path(os.getenv("XDG_STATE_HOME", Path.home() / ".local/state")) / "forthwall/backups"
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
        backup = directory / f"{self.session_id}-{block_id}-{stamp}.cs"
        fd = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8", newline="") as output:
            output.write(before.source)
        try:
            result = self._call("deploy", pb.Deploy(block_id=block_id, source=source,
                                run_after_compile=run, argument=argument,
                                expected_source_sha256=before.sha256 if expected_sha256 is None else expected_sha256), hub_id)
        finally:
            self.clear_cache()
        return Deployment(result.status, backup)

    def deploy_file(self, block_id: int, path, **options) -> Deployment:
        with Path(path).open("rb") as source:
            data = source.read(256 * 1024 + 1)
        if len(data) > 256 * 1024:
            raise ValueError("Source exceeds 256 KiB.")
        return self.deploy(block_id, data.decode("utf-8-sig"), **options)

    def run(self, block_id: int, argument="") -> pb.BlockStatus:
        return self._mutate("run", pb.Run(block_id=block_id, argument=argument)).status

    def set_waypoints(self, block_id: int, waypoints: Iterable[Waypoint], *, speed_limit=10.0,
                      flight_pattern="one-way", collision_avoidance=True, docking_mode=False):
        """Replace the route and leave autopilot off. Coordinates are world metres."""
        modes = {"one-way": pb.ONE_WAY, "patrol": pb.PATROL, "circle": pb.CIRCLE}
        if flight_pattern not in modes or not math.isfinite(speed_limit) or not 0 < speed_limit <= 100:
            raise ValueError("Use one-way/patrol/circle and a speed in (0, 100] m/s.")
        command = pb.SetWaypoints(block_id=block_id, speed_limit=speed_limit, flight_pattern=modes[flight_pattern],
                                  disable_collision_avoidance=not collision_avoidance, docking_mode=docking_mode)
        for point in waypoints:
            if len(command.waypoints) >= 32:
                raise ValueError("Use at most 32 waypoints.")
            if len(point.name) > 128 or not all(math.isfinite(v) and abs(v) <= 1e12 for v in (point.x, point.y, point.z)):
                raise ValueError("Waypoints need a short name and finite world coordinates within +/-1e12 m.")
            command.waypoints.add().CopyFrom(point)
        return self._mutate("set_waypoints", command).control_status

    def autopilot(self, block_id: int, enabled: bool, *, on_link_loss="continue", dampeners_on_stop=True) -> pb.ControlStatus:
        if on_link_loss not in ("continue", "stop"):
            raise ValueError("on_link_loss must be 'continue' or 'stop'.")
        return self._mutate("set_autopilot", pb.SetAutopilot(block_id=block_id, enabled=enabled,
                            stop_on_link_loss=on_link_loss == "stop", dampeners_on_stop=dampeners_on_stop)).control_status
