#!/usr/bin/env python3
"""Read-only terminal dashboard. Run scripts/forthwall-dashboard from the repo."""
from __future__ import annotations

import argparse
import curses
import json
import math
from pathlib import Path
import queue
import sys
import threading
import time
import unicodedata

from forthwall import Gateway, StatusMonitor


def clean(value) -> str:
    # Block names and PB Echo output are data, never terminal escape sequences.
    return "".join(c if c.isprintable() and unicodedata.category(c) != "Cf" else " " for c in str(value))


def age(row):
    value = row["age_seconds"]
    return "--" if value is None else f"{value:.0f}s"


def label(row):
    if row["error"]:
        return row["error"]
    if not row["present"]:
        return "UNAVAILABLE"
    return ("~" if row["stale"] else "") + row["state"]


def plain(snapshot) -> str:
    hub = snapshot["hub"]
    lines = ["FORTH WALL ATTENNA | " + ("CONNECTED" if snapshot["connected"] else "WAITING"),
             "Hub: " + (f"{hub['name']} [{hub['id']}] / {hub['grid']}" if hub else "none selected")]
    if snapshot["error"]:
        lines.append(snapshot["error"])
    for row in snapshot["rows"]:
        lines.append(f"{row['kind']:2}  {row['id']}  {row['name']} / {row['grid']} | {label(row)} | {row['detail']} | age {age(row)}")
        if row["output"]:
            lines.append("    Output: " + row["output"][:400])
        for diagnostic in row["diagnostics"][:2]:
            lines.append("    Diagnostic: " + diagnostic[:400])
    if not snapshot["rows"]:
        lines.append("No reachable PBs or Remote Controls sampled yet.")
    cache = snapshot["cache"]
    lines.append(f"Cache: {cache['entries']}/{cache['capacity']} entries | {cache['hits']} hits / {cache['misses']} reads")
    lines.append("~ = cached/stale status. No commands are sent by this dashboard.")
    return "\n".join(clean(line) for line in lines)


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--uri", default="ws://127.0.0.1:18751/forthwall")
    result.add_argument("--token-file", type=Path)
    result.add_argument("--hub", type=int, help="hub entity ID when multiple hubs are available")
    result.add_argument("--interval", type=float, default=2.0, help="seconds between status polls (default 2)")
    result.add_argument("--discovery-interval", type=float, default=10.0, help="seconds between antenna discovery scans (default 10)")
    result.add_argument("--budget", type=int, default=4, help="maximum status reads per poll, 1-32 (default 4)")
    result.add_argument("--max-blocks", type=int, default=256, help="maximum retained block rows, 1-512 (default 256)")
    result.add_argument("--timeout-ms", type=int, default=2000)
    result.add_argument("--once", action="store_true", help="print one bounded sample and exit")
    result.add_argument("--json", action="store_true", help="print one JSON snapshot and exit")
    return result


def draw(screen, snapshot, selected, offset, colors, interval):
    screen.erase()
    height, width = screen.getmaxyx()

    def put(y, x, text, style=0):
        if 0 <= y < height and 0 <= x < width - 1:
            try:
                screen.addnstr(y, x, clean(text), width - x - 1, style)
            except curses.error:
                pass  # Resize or wide Unicode text at the terminal edge.

    if height < 15 or width < 65:
        put(0, 0, "Forth Wall Attenna - enlarge terminal to at least 65 x 15.")
        put(2, 0, "q quit | r refresh | use --once for plain text")
        screen.refresh()
        return offset
    good = colors["good"] if snapshot["connected"] else colors["warn"]
    put(0, 1, "FORTH WALL ATTENNA", colors["title"] | curses.A_BOLD)
    put(0, max(25, width - 22), "CONNECTED" if snapshot["connected"] else "WAITING / STALE", good | curses.A_BOLD)
    hub = snapshot["hub"]
    put(2, 1, f"Hub: {hub['name']} [{hub['id']}]" if hub else "Hub: none selected")
    reachable = [row for row in snapshot["rows"] if row["present"] and not row["error"]]
    pbs = sum(row["kind"] == "PB" for row in reachable)
    rcs = sum(row["kind"] == "RC" for row in reachable)
    cache = snapshot["cache"]
    put(3, 1, f"PBs {pbs}  |  Controllers {rcs}  |  Cache {cache['entries']}/{cache['capacity']}  |  {snapshot['status_budget']} reads / {interval:g}s")
    if snapshot["error"]:
        put(4, 1, snapshot["error"], colors["warn"])
    else:
        put(4, 1, f"Discovery every {snapshot['discovery_interval']:g}s | ~ means cached/stale | antenna routes checked by the game", colors["muted"])
    put(6, 1, "TYPE  BLOCK / GRID                  STATE             AGE", colors["title"])
    rows = snapshot["rows"]
    table_height = max(1, height - 15)
    offset = max(0, min(offset, max(0, len(rows) - table_height)))
    if selected < offset:
        offset = selected
    elif selected >= offset + table_height:
        offset = selected - table_height + 1
    for i, row in enumerate(rows[offset:offset + table_height], offset):
        style = colors["warn"] if row["stale"] or row["error"] else colors["good"]
        if i == selected:
            style |= curses.A_REVERSE
        name = clean(row["name"] + (" / " + row["grid"] if row["grid"] else ""))[:28]
        line = f"{row['kind']:4}  {name:28}  {label(row):16.16}  {age(row):>5}"
        if width >= 100:
            line += "  " + row["detail"]
        put(7 + i - offset, 1, line, style)
    if not rows:
        put(7, 1, "No blocks yet. Build a powered hub and connect owned blocks by antennas.", colors["muted"])
        if snapshot["hubs"]:
            put(8, 1, "Available hubs: " + ", ".join(h["id"] for h in snapshot["hubs"]))
    detail_y = height - 7
    put(detail_y, 1, "-" * (width - 3), colors["muted"])
    if rows:
        row = rows[selected]
        put(detail_y + 1, 1, f"{row['kind']} ID {row['id']} | {row['name']} | {row['grid']}", colors["title"])
        put(detail_y + 2, 1, "LAST KNOWN: " + row["detail"] if row["stale"] else row["detail"])
        if row["kind"] == "PB":
            text = row["diagnostics"][0] if row["diagnostics"] else row["output"] or "No PB output."
            put(detail_y + 3, 1, text, colors["warn"] if row["diagnostics"] else 0)
            put(detail_y + 4, 1, "Source SHA-256: " + row.get("source_sha256", "not sampled"), colors["muted"])
        else:
            point = row.get("waypoint", {})
            put(detail_y + 3, 1, f"Waypoint: {point.get('name') or 'none'} | Dampeners: {'on' if row.get('dampeners') else 'off'}")
            if point:
                put(detail_y + 4, 1, f"Waypoint position: {point['x']:.1f}, {point['y']:.1f}, {point['z']:.1f}", colors["muted"])
    suffix = f" | {snapshot['truncated']} blocks beyond limit" if snapshot["truncated"] else ""
    put(height - 1, 1, f"q quit | r refresh | arrows scroll | READ ONLY{suffix}", colors["muted"])
    screen.refresh()
    return offset


def terminal(screen, monitor, interval):
    try:
        curses.curs_set(0)
    except curses.error:
        pass
    screen.timeout(200)
    colors = dict.fromkeys(("title", "good", "warn", "muted"), 0)
    if curses.has_colors():
        curses.start_color()
        background = curses.COLOR_BLACK
        try:
            curses.use_default_colors()
            background = -1
        except curses.error:
            pass
        for i, (name, color) in enumerate((("title", curses.COLOR_CYAN), ("good", curses.COLOR_GREEN),
                                         ("warn", curses.COLOR_YELLOW), ("muted", curses.COLOR_WHITE)), 1):
            curses.init_pair(i, color, background)
            colors[name] = curses.color_pair(i)
    updates = queue.Queue(maxsize=1)
    stop, refresh = threading.Event(), threading.Event()

    def worker():
        try:
            while not stop.is_set():
                if refresh.is_set():
                    refresh.clear()
                    monitor.refresh()
                snapshot = monitor.poll()
                try:
                    updates.get_nowait()
                except queue.Empty:
                    pass
                updates.put_nowait(snapshot)
                deadline = time.monotonic() + interval
                while not stop.is_set() and not refresh.is_set() and time.monotonic() < deadline:
                    stop.wait(min(.1, max(0, deadline - time.monotonic())))
        finally:
            monitor.close()

    thread = threading.Thread(target=worker, name="forthwall-status", daemon=True)
    snapshot = monitor.snapshot()
    thread.start()
    selected = offset = 0
    dirty = True
    try:
        while True:
            try:
                snapshot = updates.get_nowait()
                dirty = True
            except queue.Empty:
                pass
            selected = max(0, min(selected, len(snapshot["rows"]) - 1))
            if dirty:
                offset = draw(screen, snapshot, selected, offset, colors, interval)
                dirty = False
            key = screen.getch()
            if key in (ord("q"), ord("Q"), 27):
                break
            if key in (ord("r"), ord("R")):
                refresh.set()
            elif key in (curses.KEY_DOWN, ord("j")):
                selected += 1
            elif key in (curses.KEY_UP, ord("k")):
                selected -= 1
            elif key == curses.KEY_NPAGE:
                selected += max(1, screen.getmaxyx()[0] - 15)
            elif key == curses.KEY_PPAGE:
                selected -= max(1, screen.getmaxyx()[0] - 15)
            dirty |= key != -1
    finally:
        stop.set()
        thread.join(timeout=1)


def main(argv=None):
    arg_parser = parser()
    args = arg_parser.parse_args(argv)
    if not math.isfinite(args.interval) or args.interval < .5:
        arg_parser.error("--interval must be finite and at least 0.5 seconds")
    if not math.isfinite(args.discovery_interval) or args.discovery_interval < args.interval:
        arg_parser.error("--discovery-interval must be finite and at least --interval")
    if not 1 <= args.budget <= 32 or not 1 <= args.max_blocks <= 512 or not 100 <= args.timeout_ms <= 30000:
        arg_parser.error("use --budget 1-32, --max-blocks 1-512, and --timeout-ms 100-30000")
    try:
        gateway = Gateway(args.uri, args.token_file, args.hub, args.timeout_ms, discovery_ttl=args.discovery_interval)
        monitor = StatusMonitor(gateway, discovery_interval=args.discovery_interval, status_budget=args.budget,
                                max_blocks=args.max_blocks, stale_after=max(5, args.interval * 2))
    except ValueError:
        arg_parser.error("use a local ws://127.0.0.1:PORT/forthwall endpoint and valid cache settings")
    if args.once or args.json or not (sys.stdin.isatty() and sys.stdout.isatty()):
        try:
            snapshot = monitor.poll()
            print(json.dumps(snapshot, indent=2) if args.json else plain(snapshot))
            return 0 if snapshot["connected"] else 1
        finally:
            monitor.close()
    try:
        curses.wrapper(terminal, monitor, args.interval)
    except KeyboardInterrupt:
        pass
    except curses.error:
        print("Terminal initialization failed. Run with --once for plain output.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
