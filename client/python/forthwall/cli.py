from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys

from google.protobuf.json_format import MessageToDict
from websockets.exceptions import WebSocketException
from .client import Client, GatewayError
from . import forthwall_pb2 as pb


def parser():
    p = argparse.ArgumentParser(description="Forth Wall Attenna: owned PBs and Remote Control blocks over your antenna network")
    p.add_argument("--uri", default="ws://127.0.0.1:18751/forthwall")
    p.add_argument("--token-file", type=Path)
    p.add_argument("--hub", type=int, help="hub entity ID; optional when exactly one working hub exists")
    p.add_argument("--timeout-ms", type=int, default=10000)
    commands = p.add_subparsers(dest="command", required=True)
    for name in ("hubs", "discover", "controls"):
        commands.add_parser(name)
    for name in ("status", "control-status", "run", "read", "deploy", "waypoints", "autopilot"):
        sub = commands.add_parser(name)
        sub.add_argument("block", type=int)
        if name == "run":
            sub.add_argument("argument", nargs="?", default="")
        elif name == "read":
            sub.add_argument("--output", type=Path, required=True)
        elif name == "deploy":
            sub.add_argument("source", type=Path)
            sub.add_argument("--run", action="store_true", help="explicitly invoke Main after compilation")
            sub.add_argument("--argument", default="")
            sub.add_argument("--backup-dir", type=Path, default=Path(os.getenv("XDG_STATE_HOME", Path.home() / ".local/state")) / "forthwall/backups")
        elif name == "waypoints":
            sub.add_argument("file", type=Path, help='JSON array: [{"name":"Base", "x":0, "y":0, "z":0}]')
            sub.add_argument("--speed", type=float, default=10)
            sub.add_argument("--flight", choices=("one-way", "patrol", "circle"), default="one-way")
            sub.add_argument("--disable-collision-avoidance", action="store_true")
            sub.add_argument("--docking", action="store_true")
        elif name == "autopilot":
            sub.add_argument("state", choices=("start", "stop"))
            sub.add_argument("--on-link-loss", choices=("continue", "stop"), default="continue")
            sub.add_argument("--coast-on-stop", action="store_true", help="leave dampeners unchanged when stopping")
    return p


def show(response):
    print(json.dumps(MessageToDict(response, preserving_proto_field_name=True), indent=2))


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        with Client(args.uri, args.token_file, args.timeout_ms) as client:
            hubs = client.hubs()
            if args.command == "hubs":
                show(pb.Response(session_id=client.session_id, blocks=pb.Blocks(blocks=hubs)))
                return 0
            available = [h for h in hubs if h.working]
            hub = args.hub
            if hub is None:
                if len(available) != 1:
                    raise ValueError(f"Found {len(available)} working hubs. Use 'forthwall hubs' and select --hub ID.")
                hub = available[0].entity_id
            request = lambda op, msg: client.request(op, msg, hub)
            if args.command == "discover":
                response = request("discover", pb.Discover())
            elif args.command == "controls":
                response = request("discover_controls", pb.DiscoverControls())
            elif args.command == "status":
                response = request("get_status", pb.GetStatus(block_id=args.block))
            elif args.command == "control-status":
                response = request("get_control_status", pb.GetControlStatus(block_id=args.block))
            elif args.command == "run":
                response = request("run", pb.Run(block_id=args.block, argument=args.argument))
            elif args.command == "read":
                response = request("read_program", pb.ReadProgram(block_id=args.block))
                # Exclusive creation avoids accidentally overwriting a local source file.
                with args.output.open("x", encoding="utf-8", newline="") as output:
                    output.write(response.program.source)
                print(f"Saved source to {args.output}; SHA-256 {response.program.sha256}")
                return 0
            elif args.command == "deploy":
                source_bytes = args.source.read_bytes()
                if len(source_bytes) > 256 * 1024:
                    raise ValueError("Source exceeds 256 KiB.")
                source = source_bytes.decode("utf-8-sig")
                before = request("read_program", pb.ReadProgram(block_id=args.block)).program
                args.backup_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
                stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
                backup = args.backup_dir / f"{client.session_id}-{args.block}-{stamp}.cs"
                fd = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                with os.fdopen(fd, "w", encoding="utf-8", newline="") as output:
                    output.write(before.source)
                print(f"Previous source backed up to {backup}", file=sys.stderr)
                response = request("deploy", pb.Deploy(block_id=args.block, source=source, run_after_compile=args.run,
                                   argument=args.argument, expected_source_sha256=before.sha256))
            elif args.command == "waypoints":
                data = json.loads(args.file.read_text())
                if not isinstance(data, list) or len(data) > 32:
                    raise ValueError("Waypoints must be a JSON array with at most 32 entries.")
                route = pb.SetWaypoints(block_id=args.block, speed_limit=args.speed,
                                       flight_pattern={"one-way": pb.ONE_WAY, "patrol": pb.PATROL, "circle": pb.CIRCLE}[args.flight],
                                       disable_collision_avoidance=args.disable_collision_avoidance, docking_mode=args.docking)
                for point in data:
                    route.waypoints.add(**point)
                response = request("set_waypoints", route)
            else:
                response = request("set_autopilot", pb.SetAutopilot(block_id=args.block, enabled=args.state == "start",
                                   dampeners_on_stop=not args.coast_on_stop, stop_on_link_loss=args.on_link_loss == "stop"))
            show(response)
            return 0
    except GatewayError as error:
        show(error.response)
        return 2
    except (OSError, ValueError, TypeError, TimeoutError, WebSocketException) as error:
        # Transport exception text may include handshake headers; never print it.
        if isinstance(error, WebSocketException):
            print(f"Connection failed ({type(error).__name__}); check the gateway log. Mutations are not retried.", file=sys.stderr)
        else:
            print(f"{type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
