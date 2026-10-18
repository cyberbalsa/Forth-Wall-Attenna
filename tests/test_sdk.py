"""SDK/CLI integration against the C# gateway, plus monitor lifecycle scenarios."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

from forthwall import CacheInfo, Gateway, GatewayError, HubSelectionError, StatusMonitor, Waypoint
from forthwall import forthwall_pb2 as pb
from test_client import GatewayFixture, ROOT


class SdkTests(GatewayFixture):
    def gateway(self, **options):
        return Gateway(self.uri, self.token_file, **options)

    def test_cache_copies_bounds_and_forced_refresh(self):
        with self.gateway(cache_size=3, discovery_ttl=60, status_ttl=60) as game:
            with patch.object(game.client, 'request', wraps=game.client.request) as request:
                first = game.programs()
                first[0].name = 'caller edit'
                self.assertNotEqual(game.programs()[0].name, 'caller edit')
                self.assertEqual(sum(c.args[0] == 'discover' for c in request.call_args_list), 1)
                game.status(2)
                game.status(2)
                self.assertEqual(sum(c.args[0] == 'get_status' for c in request.call_args_list), 1)
                game.status(2, refresh=True)
                self.assertEqual(sum(c.args[0] == 'get_status' for c in request.call_args_list), 2)
                game.controllers()
                game.control_status(3)
                self.assertLessEqual(game.cache_info.entries, 3)
                self.assertGreater(game.cache_info.hits, 0)
            game.close()
            self.assertEqual(game.cache_info.entries, 0)
            with self.assertRaises(RuntimeError):
                game.hubs()

    def test_sdk_deploy_backup_conflict_run_and_waypoints(self):
        with self.gateway() as game:
            previous = game.read_program(2).source
            backups = self.directory / 'sdk-backups'
            source = 'public void Main(string argument) { Echo(argument); }'
            result = game.deploy(2, source, backup_dir=backups)
            self.assertEqual(result.backup_path.read_text(), previous)
            self.assertEqual(result.backup_path.stat().st_mode & 0o777, 0o600)
            self.assertTrue(result.status.compiled)
            self.assertFalse(result.status.main_invoked)
            self.assertEqual(game.read_program(2).source, source)
            with self.assertRaises(GatewayError) as failed:
                game.deploy(2, 'conflicting source', expected_sha256='0' * 64, backup_dir=backups)
            self.assertEqual(failed.exception.response.error.code, pb.Error.SOURCE_CONFLICT)
            self.assertEqual(game.read_program(2).source, source)
            self.assertTrue(game.run(2, 'hello').main_invoked)
            result = game.set_waypoints(3, [Waypoint(name='Home', x=100, y=200, z=300)], speed_limit=12)
            self.assertFalse(result.autopilot_enabled)
            self.assertTrue(game.autopilot(3, True, on_link_loss='stop').stop_on_link_loss)
            self.assertFalse(game.autopilot(3, True, on_link_loss='continue').stop_on_link_loss)
            self.assertFalse(game.autopilot(3, False).autopilot_enabled)

    def test_ambiguous_mutation_is_not_retried_and_clears_cache(self):
        with self.gateway() as game:
            game.programs()
            original = game.client.request
            calls = []

            def request(operation, *args):
                if operation == 'run':
                    calls.append(operation)
                    raise TimeoutError('uncertain transport result')
                return original(operation, *args)

            with patch.object(game.client, 'request', side_effect=request):
                with self.assertRaises(TimeoutError):
                    game.run(2)
            self.assertEqual(calls, ['run'])
            self.assertEqual(game.cache_info.entries, 0)
            game.connect()
            self.assertEqual(game.programs()[0].entity_id, 2)

    def test_selecting_an_unavailable_hub_does_not_fall_back(self):
        with self.gateway(hub_id=999) as game:
            self.assertEqual(game.hubs()[0].entity_id, 1)
            with self.assertRaises(HubSelectionError):
                game.programs()

    def test_example_dashboard_json_and_starter_against_real_transport(self):
        common = ['--uri', self.uri, '--token-file', str(self.token_file)]
        result = subprocess.run([sys.executable, str(ROOT / 'examples/python/status_dashboard.py'), '--json', *common],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        snapshot = json.loads(result.stdout)
        self.assertTrue(snapshot['connected'])
        self.assertEqual({row['kind'] for row in snapshot['rows']}, {'PB', 'RC'})
        self.assertTrue(all(row['age_seconds'] is not None for row in snapshot['rows']))
        self.assertTrue(all(not row['stale'] for row in snapshot['rows']))
        result = subprocess.run([sys.executable, str(ROOT / 'examples/python/getting_started.py'), *common],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertIn('PB 2:', result.stdout)
        self.assertIn('RC 3:', result.stdout)

    def test_auth_failure_is_useful_without_disclosing_token(self):
        token = self.directory / 'wrong.token'
        secret = 'synthetic-wrong-credential-for-error-redaction-123456'
        token.write_text(secret)
        result = subprocess.run([sys.executable, str(ROOT / 'examples/python/status_dashboard.py'), '--json',
                                 '--uri', self.uri, '--token-file', str(token)],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        snapshot = json.loads(result.stdout)
        self.assertFalse(snapshot['connected'])
        self.assertIn('Gateway unavailable', snapshot['error'])
        self.assertNotIn(secret, result.stdout + result.stderr)
        self.assertNotIn('Authorization', result.stdout + result.stderr)


class ReadOnlyGateway:
    """Changing-world fixture with no mutation methods."""
    def __init__(self):
        self.world = self.session_id = 'world-a'
        self.ids = [2, 3, 4, 5, 6]
        self.reads = []
        self.failure = None
        self.switch_during_read = False
        self.cache_info = CacheInfo(0, 0, 0, 256)

    def connect(self):
        return self

    def close(self):
        pass

    def clear_cache(self):
        pass

    def hubs(self, **_):
        if self.failure:
            raise self.failure
        self.session_id = self.world
        return (pb.Block(entity_id=1, hub=True, working=True),)

    def hub(self):
        return self.hubs()[0]

    def programs(self, **_):
        return tuple(pb.Block(entity_id=i, name=f'PB {i}', grid_name=self.world, working=True) for i in self.ids)

    def controllers(self, **_):
        return ()

    def status(self, block_id, **_):
        self.reads.append(block_id)
        if self.failure:
            raise self.failure
        if self.switch_during_read:
            self.session_id = 'world-b'
        return pb.BlockStatus(block_id=block_id, compiled=True, initialized=True, working=True,
                              termination_reason='None', output='last known output')


class MonitorTests(unittest.TestCase):
    def setUp(self):
        self.now = 100.0
        self.clock = patch('forthwall.monitor.time.monotonic', side_effect=lambda: self.now)
        self.clock.start()
        self.addCleanup(self.clock.stop)
        self.gateway = ReadOnlyGateway()

    def test_round_robin_budget_and_missing_route_retention(self):
        monitor = StatusMonitor(self.gateway, status_budget=2)
        first = monitor.poll()
        self.assertEqual(len(self.gateway.reads), 2)
        self.assertEqual(sum(row['age_seconds'] is not None for row in first['rows']), 2)
        self.now += 2
        monitor.poll()
        self.now += 2
        monitor.poll()
        self.assertEqual(self.gateway.reads, [2, 3, 4, 5, 6, 2])
        self.gateway.ids.remove(2)
        self.now = 111
        row = next(row for row in monitor.poll()['rows'] if row['id'] == '2')
        self.assertFalse(row['present'])
        self.assertTrue(row['stale'])
        self.assertEqual(row['output'], 'last known output')
        self.now = 162
        self.assertNotIn('2', [row['id'] for row in monitor.poll()['rows']])

    def test_disconnect_backoff_stale_data_and_new_world(self):
        monitor = StatusMonitor(self.gateway)
        monitor.poll()
        self.gateway.failure = ConnectionRefusedError('DO NOT PRINT authentication header')
        self.now += 2
        snapshot = monitor.poll()
        self.assertFalse(snapshot['connected'])
        self.assertTrue(all(row['stale'] for row in snapshot['rows']))
        self.assertNotIn('DO NOT PRINT', snapshot['error'])
        reads = len(self.gateway.reads)
        self.now += 1
        monitor.poll()
        self.assertEqual(len(self.gateway.reads), reads)
        self.gateway.failure = None
        self.gateway.world, self.gateway.ids = 'world-b', [42]
        self.now += 2
        snapshot = monitor.poll()
        self.assertTrue(snapshot['connected'])
        self.assertEqual([row['id'] for row in snapshot['rows']], ['42'])
        self.assertEqual(snapshot['session_id'], 'world-b')

    def test_world_unload_and_mid_poll_world_switch_clear_old_rows(self):
        monitor = StatusMonitor(self.gateway)
        monitor.poll()
        self.gateway.failure = GatewayError(pb.Response(error=pb.Error(code=pb.Error.WORLD_UNLOADED)))
        self.now += 2
        self.assertEqual(monitor.poll()['rows'], [])
        self.gateway.failure = None
        self.gateway.world = 'world-b'
        self.now += 3
        self.assertTrue(monitor.poll()['connected'])
        self.gateway.world = 'world-c'
        self.now += 2
        # Establish c, then simulate a new world appearing during a status read.
        monitor.refresh()
        monitor.poll()
        self.gateway.switch_during_read = True
        self.now += 2
        snapshot = monitor.poll()
        self.assertEqual(snapshot['rows'], [])
        self.assertFalse(snapshot['connected'])

    def test_memory_cap_and_terminal_escape_sanitization(self):
        monitor = StatusMonitor(self.gateway, max_blocks=2)
        snapshot = monitor.poll()
        self.assertEqual(len(snapshot['rows']), 2)
        self.assertEqual(snapshot['truncated'], 3)
        spec = importlib.util.spec_from_file_location('dashboard_example', ROOT / 'examples/python/status_dashboard.py')
        dashboard = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(dashboard)
        text = dashboard.clean('name\x1b[2J\r\b\u202ehidden')
        self.assertNotIn('\x1b', text)
        self.assertNotIn('\r', text)
        self.assertNotIn('\u202e', text)


if __name__ == '__main__':
    unittest.main()
