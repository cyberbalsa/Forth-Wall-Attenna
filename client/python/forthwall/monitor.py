"""Read-only, bounded polling that can be reused by dashboards and small tools."""
from __future__ import annotations

from dataclasses import dataclass
import math
import time

from websockets.exceptions import WebSocketException

from .client import GatewayError
from .gateway import Gateway, HubSelectionError
from . import forthwall_pb2 as pb


def error_summary(error: Exception) -> str:
    """Human-readable failures without transport headers or authentication data."""
    if isinstance(error, GatewayError):
        code = pb.Error.Code.Name(error.response.error.code)
        hints = {
            "NO_WORLD": "Load a local single-player world.",
            "WORLD_UNLOADED": "Waiting for a world to load.",
            "WRONG_SESSION": "World changed; refreshing discovery.",
            "TIMEOUT": "Game may be paused or busy.",
            "NOT_WORKING": "Power on the selected hub.",
            "ACCESS_DENIED": "Check local block ownership.",
        }
        return code + ": " + hints.get(code, "The gateway could not complete this read.")
    if isinstance(error, HubSelectionError):
        return str(error)
    if isinstance(error, FileNotFoundError):
        return "Token file not found. Launch the plugin or set --token-file."
    if isinstance(error, PermissionError):
        return "Cannot read the local token file. Check its permissions."
    if isinstance(error, (OSError, TimeoutError, WebSocketException)):
        return f"Gateway unavailable ({type(error).__name__}). Check that the game and plugin are running."
    return f"Client error ({type(error).__name__}). Check the endpoint and local configuration."


@dataclass
class _Row:
    block: pb.Block
    seen: float
    present: bool = True
    status: pb.BlockStatus | pb.ControlStatus | None = None
    sampled: float | None = None
    error: str = ""


class StatusMonitor:
    """Call poll() periodically from one thread. It never sends mutations.

    Discovery refreshes every discovery_interval seconds. Each poll samples at
    most status_budget blocks, in round-robin order. Last-known rows survive a
    lost route for retention seconds, with a hard max_blocks memory limit.
    Read failures back off up to 30 seconds; session changes discard old data.
    """

    def __init__(self, gateway: Gateway, *, discovery_interval=10.0, status_budget=4,
                 max_blocks=256, retention=60.0, stale_after=5.0):
        if not all(math.isfinite(v) and v > 0 for v in (discovery_interval, retention, stale_after)):
            raise ValueError("Polling and retention intervals must be finite and positive.")
        if not isinstance(status_budget, int) or not isinstance(max_blocks, int) or not 1 <= status_budget <= 32 or not 1 <= max_blocks <= 512:
            raise ValueError("Use 1-32 status reads per poll and a 1-512 block limit.")
        self.gateway = gateway
        self.discovery_interval, self.status_budget = discovery_interval, status_budget
        self.max_blocks, self.retention, self.stale_after = max_blocks, retention, stale_after
        self.rows: dict[int, _Row] = {}
        self.hubs: tuple[pb.Block, ...] = ()
        self.hub: pb.Block | None = None
        self.session_id = ""
        self.connected = False
        self.error = "Waiting for the gateway."
        self.total_found = 0
        self._cursor = 0
        self._next_discovery = self._retry_at = 0.0
        self._backoff = 2.0

    def refresh(self):
        """Request a fresh read on the next poll, without replaying any command."""
        self.gateway.clear_cache()
        self._next_discovery = self._retry_at = 0.0

    def close(self):
        self.gateway.close()

    def _discover(self, now):
        self.hubs = self.gateway.hubs(refresh=True)
        if self.session_id != self.gateway.session_id:
            self.rows.clear()
            self.hub = None
            self._cursor = 0
            self.session_id = self.gateway.session_id
        selected = self.gateway.hub()
        if self.hub is not None and selected.entity_id != self.hub.entity_id:
            self.rows.clear()
            self._cursor = 0
        self.hub = selected
        blocks = (*self.gateway.programs(refresh=True), *self.gateway.controllers(refresh=True))
        self.total_found = len(blocks)
        for row in self.rows.values():
            row.present = False
        # Active blocks take priority over retained stale rows at the memory cap.
        updated = {}
        for block in blocks[:self.max_blocks]:
            row = self.rows.get(block.entity_id, _Row(block, now))
            row.block, row.seen, row.present = block, now, True
            updated[block.entity_id] = row
        for block_id, row in self.rows.items():
            if block_id not in updated and now - row.seen <= self.retention and len(updated) < self.max_blocks:
                updated[block_id] = row
        self.rows = updated
        self._next_discovery = now + self.discovery_interval

    def poll(self) -> dict:
        now = time.monotonic()
        # Expire missing rows even during a long gateway outage.
        self.rows = {key: row for key, row in self.rows.items()
                     if (self.connected and row.present) or now - row.seen <= self.retention}
        if now < self._retry_at:
            return self.snapshot()
        try:
            self.gateway.connect()
            if now >= self._next_discovery:
                self._discover(now)
            active = [row for row in self.rows.values() if row.present]
            for _ in range(min(self.status_budget, len(active))):
                self._cursor %= len(active)
                row = active[self._cursor]
                self._cursor += 1
                try:
                    read = self.gateway.control_status if row.block.remote_control else self.gateway.status
                    row.status = read(row.block.entity_id, refresh=True)
                    row.sampled = time.monotonic()
                    row.error = ""
                except GatewayError as error:
                    code = error.response.error.code
                    if code not in (pb.Error.NOT_FOUND, pb.Error.UNREACHABLE, pb.Error.ACCESS_DENIED, pb.Error.NOT_WORKING):
                        raise
                    row.error = pb.Error.Code.Name(code)
                # A cache-expired hub lookup may observe a world switch between
                # discovery passes. Never publish old row labels for that world.
                if self.gateway.session_id != self.session_id:
                    self.rows.clear()
                    self.hub, self.hubs, self.session_id, self.total_found = None, (), "", 0
                    self.connected, self.error = False, "World changed; refreshing discovery."
                    self._next_discovery = 0.0
                    return self.snapshot()
            self.connected, self.error = True, ""
            self._backoff, self._retry_at = 2.0, 0.0
        except (GatewayError, HubSelectionError, OSError, TimeoutError, WebSocketException, ValueError) as error:
            self.connected, self.error = False, error_summary(error)
            if isinstance(error, GatewayError) and error.response.error.code in (pb.Error.NO_WORLD, pb.Error.WORLD_UNLOADED, pb.Error.WRONG_SESSION):
                self.rows.clear()
                self.hub, self.hubs, self.session_id, self.total_found = None, (), "", 0
            self.gateway.close()
            self._next_discovery = 0.0
            self._retry_at = time.monotonic() + self._backoff
            self._backoff = min(30.0, self._backoff * 2)
        return self.snapshot()

    def snapshot(self) -> dict:
        now = time.monotonic()
        rows = []
        for row in self.rows.values():
            block, status = row.block, row.status
            age = None if row.sampled is None else max(0, now - row.sampled)
            item = {
                "id": str(block.entity_id), "kind": "RC" if block.remote_control else "PB",
                "name": block.name or f"Block {block.entity_id}", "grid": block.grid_name,
                "present": row.present, "error": row.error,
                "stale": not self.connected or not row.present or bool(row.error) or age is None or age > self.stale_after,
                "age_seconds": None if age is None else round(age, 1),
                "state": "WAITING", "detail": "Awaiting first status sample", "output": "", "diagnostics": [],
            }
            if isinstance(status, pb.BlockStatus):
                item.update(state=("NO POWER" if not status.working else "RUNNING" if status.running else
                                   "READY" if status.initialized else "NO PROGRAM" if not status.compiled else "STOPPED"),
                            detail=status.termination_reason or "No termination status",
                            output=status.output, diagnostics=list(status.diagnostics),
                            compiled=status.compiled, source_sha256=status.source_sha256)
            elif isinstance(status, pb.ControlStatus):
                item.update(state="NO POWER" if not block.working else "PILOTED" if status.under_control else "AUTOPILOT" if status.autopilot_enabled else "IDLE",
                            detail=f"{status.speed:.1f} m/s | link loss: {'stop' if status.stop_on_link_loss else 'continue'}",
                            speed=status.speed, speed_limit=status.speed_limit, dampeners=status.dampeners,
                            autopilot=status.autopilot_enabled, stop_on_link_loss=status.stop_on_link_loss,
                            waypoint={"name": status.current_waypoint.name, "x": status.current_waypoint.x,
                                      "y": status.current_waypoint.y, "z": status.current_waypoint.z})
            rows.append(item)
        cache = self.gateway.cache_info
        return {
            "connected": self.connected, "error": self.error, "session_id": self.session_id,
            "hub": None if self.hub is None else {"id": str(self.hub.entity_id), "name": self.hub.name, "grid": self.hub.grid_name, "working": self.hub.working},
            "hubs": [{"id": str(h.entity_id), "name": h.name, "working": h.working} for h in self.hubs],
            "rows": rows, "total_found": self.total_found, "truncated": max(0, self.total_found - self.max_blocks),
            "retry_in_seconds": round(max(0, self._retry_at - now), 1),
            "status_budget": self.status_budget, "discovery_interval": self.discovery_interval,
            "cache": {"hits": cache.hits, "misses": cache.misses, "entries": cache.entries, "capacity": cache.capacity},
        }
