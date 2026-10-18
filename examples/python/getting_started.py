#!/usr/bin/env python3
"""Copy this read-only starter to begin your own Forth Wall Attenna program."""
import argparse
from pathlib import Path

from forthwall import Gateway, GatewayError, HubSelectionError
from forthwall.monitor import error_summary
from websockets.exceptions import WebSocketException


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--hub", type=int, help="required when there are multiple working hubs")
    parser.add_argument("--uri", default="ws://127.0.0.1:18751/forthwall")
    parser.add_argument("--token-file", type=Path)
    args = parser.parse_args()
    try:
        with Gateway(args.uri, args.token_file, hub_id=args.hub) as game:
            hub = game.hub()
            print(f"Connected to {hub.name or 'hub'} ({hub.entity_id})")
            for block in game.programs():
                status = game.status(block.entity_id)
                print(f"PB {block.entity_id}: {block.name!r}; ready={status.initialized}, powered={status.working}")
                print(f"  Output: {status.output!r}")
            for block in game.controllers():
                status = game.control_status(block.entity_id)
                print(f"RC {block.entity_id}: {block.name!r}; speed={status.speed:.1f} m/s, autopilot={status.autopilot_enabled}")
            # See README.md beside this file for guarded deployment and waypoint
            # examples. This starter deliberately makes no changes to the world.
    except (GatewayError, HubSelectionError, OSError, TimeoutError, ValueError, WebSocketException) as error:
        print(error_summary(error))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
