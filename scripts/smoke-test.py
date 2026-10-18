#!/usr/bin/env python3
"""Live checks restricted to the generated relay test world's known entities."""
from pathlib import Path
import time

from forthwall import Client, GatewayError
from forthwall import forthwall_pb2 as pb

ROOT = Path(__file__).resolve().parents[1]
HUB = 130000000000000044
TARGET = 130000000000000131
CONTROL = 130000000000000132


def main():
    with Client(timeout_ms=30000) as client:
        hubs = client.hubs()
        assert any(h.entity_id == HUB and h.grid_name == 'Hub Station' for h in hubs), 'Load the generated relay test world first.'
        request = lambda name, value: client.request(name, value, HUB)
        blocks = request('discover', pb.Discover()).blocks.blocks
        assert any(b.entity_id == TARGET and b.grid_name == 'Remote Test Ship' for b in blocks)
        before = request('read_program', pb.ReadProgram(block_id=TARGET)).program
        backup = ROOT / 'artifacts/live-test-source-backup.cs'
        backup.parent.mkdir(exist_ok=True)
        backup.write_text(before.source)
        print('PASS live hub + PB + relay discovery', flush=True)
        try:
            try:
                request('deploy', pb.Deploy(block_id=TARGET, source='should not be assigned', expected_source_sha256='0'*64))
                raise AssertionError('Stale source guard did not reject the write')
            except GatewayError as error:
                assert error.response.error.code == pb.Error.SOURCE_CONFLICT
            print('PASS live optimistic source guard', flush=True)
            result = request('deploy', pb.Deploy(block_id=TARGET, source=(ROOT/'tests/fixtures/link-loss.cs').read_text(), expected_source_sha256=before.sha256))
            assert result.status.compiled and result.status.initialized and not result.status.main_invoked
            print('PASS live upload + compile without implicit Main', flush=True)
            route = pb.SetWaypoints(block_id=CONTROL, speed_limit=1)
            route.waypoints.add(name='Link-loss test', x=7.5, y=2.5, z=-4400)
            request('set_waypoints', route)
            for stop in (True, False):
                result = request('set_autopilot', pb.SetAutopilot(block_id=CONTROL, enabled=True, stop_on_link_loss=stop))
                assert result.control_status.autopilot_enabled
                try:
                    request('run', pb.Run(block_id=TARGET, argument='drop'))
                except GatewayError as error:
                    assert error.response.error.code == pb.Error.UNREACHABLE
                    assert error.response.mutation_outcome == pb.APPLIED
                time.sleep(.2)
                assert not request('discover', pb.Discover()).blocks.blocks, 'PB stayed reachable during outage'
                print('PASS radio outage hides target; waiting for its timed restoration', flush=True)
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    time.sleep(.5)
                    if request('discover', pb.Discover()).blocks.blocks:
                        break
                status = request('get_status', pb.GetStatus(block_id=TARGET)).status
                expected = 'off' if stop else 'on'
                assert 'autopilot_during_outage=' + expected in status.output, status.output
                control = request('get_control_status', pb.GetControlStatus(block_id=CONTROL)).control_status
                assert control.autopilot_enabled == (not stop)
                if stop:
                    assert control.dampeners
                print('PASS live --on-link-loss ' + ('stop (dampeners on)' if stop else 'continue'), flush=True)
                request('set_autopilot', pb.SetAutopilot(block_id=CONTROL, enabled=False, dampeners_on_stop=True))
        finally:
            # The test PB restores its own antenna; never replace it during the outage.
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                try:
                    current = request('read_program', pb.ReadProgram(block_id=TARGET)).program
                    request('set_autopilot', pb.SetAutopilot(block_id=CONTROL, enabled=False, dampeners_on_stop=True))
                    request('deploy', pb.Deploy(block_id=TARGET, source=before.source, expected_source_sha256=current.sha256))
                    print('Restored original PB source and stopped test autopilot.', flush=True)
                    break
                except GatewayError as error:
                    if error.response.error.code != pb.Error.UNREACHABLE:
                        raise
                    time.sleep(.5)
            else:
                raise RuntimeError('Could not restore test source: reconnect the radio; backup is in artifacts.')


if __name__ == '__main__':
    main()
