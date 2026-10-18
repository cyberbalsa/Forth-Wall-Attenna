"""Exercise the actual Python CLI against the actual C# transport and dispatcher."""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
from steam_options import get_options, set_options
from steam_paths import steam_user


class SetupTests(unittest.TestCase):
    def test_vdf_roundtrip_preserves_other_apps_and_escaped_paths(self):
        text = '"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "other" {"LaunchOptions" "unchanged"} "244850" {"LastPlayed" "123"} } } } } }'
        value = '"/path with spaces/launch-proton.sh" %command%'
        result = set_options(text, value)
        self.assertEqual(get_options(result), value)
        self.assertIn('"other" {"LaunchOptions" "unchanged"}', result)
        self.assertEqual(set_options(result, value), result)
        self.assertEqual(get_options(set_options(result, 'new')), 'new')

    def test_launch_wrapper_preserves_argv(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory) / 'project with spaces'
            wrapper = project / 'scripts/launch-proton.sh'
            wrapper.parent.mkdir(parents=True)
            shutil.copy2(ROOT / 'scripts/launch-proton.sh', wrapper)
            loader = project / '.tools/pulsar/Legacy.exe'
            loader.parent.mkdir(parents=True)
            loader.touch()  # Only argv is exercised; no loader is executed.
            output = Path(directory) / 'argv.json'
            shim = Path(directory) / 'runner.py'
            shim.write_text('import json,sys\nopen(sys.argv[1],"w").write(json.dumps(sys.argv[2:]))\n')
            subprocess.run([str(wrapper), sys.executable, str(shim), str(output),
                            'waitforexitandrun', '/game with spaces/SpaceEngineers.exe', '-some-game-arg'], check=True)
            self.assertEqual(json.loads(output.read_text()), ['waitforexitandrun', str(loader),
                            '/game with spaces/SpaceEngineers.exe', '-some-game-arg', '-noUpdate', '-noStats', '-noDiscord'])

    def test_steam_account_selection_is_explicit_when_ambiguous(self):
        with tempfile.TemporaryDirectory() as directory:
            steam = Path(directory)
            with self.assertRaises(ValueError):
                steam_user(steam)
            first = steam / 'userdata/123/config/localconfig.vdf'
            first.parent.mkdir(parents=True)
            first.write_text('')
            self.assertEqual(steam_user(steam), '123')
            second = steam / 'userdata/456/config/localconfig.vdf'
            second.parent.mkdir(parents=True)
            second.write_text('')
            with self.assertRaises(ValueError):
                steam_user(steam)
            self.assertEqual(steam_user(steam, '456'), '456')
            for invalid in ('../123', '999', '0'):
                with self.assertRaises(ValueError):
                    steam_user(steam, invalid)


class GatewayFixture(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.directory = Path(cls.temp.name)
        token = cls.directory / 'test.token'
        token.write_text(secrets.token_hex(32))
        token.chmod(0o600)
        port = cls.directory / 'port'
        sdk = os.getenv('FORTHWALL_DOTNET', 'dotnet')
        cls.server = subprocess.Popen([sdk, str(ROOT / 'tests/ForthWall.Tests/bin/Release/net9.0/ForthWall.Tests.dll'), '--serve', str(token), str(port)], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL)
        deadline = time.monotonic() + 10
        while not port.exists():
            if cls.server.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError('C# test gateway failed to start')
            time.sleep(.01)
        cls.uri, cls.token_file = f'ws://127.0.0.1:{port.read_text()}/forthwall', token
        cls.command = [str(ROOT / '.tools/venv/bin/forthwall'), '--uri', cls.uri, '--token-file', str(token)]

    @classmethod
    def tearDownClass(cls):
        cls.server.communicate(b'\n', timeout=5)
        cls.temp.cleanup()

    def cli(self, *args):
        result = subprocess.run(self.command + list(args), capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        return result.stdout

class ClientTests(GatewayFixture):
    def test_discover_deploy_backup_and_run(self):
        hubs = json.loads(self.cli('hubs'))
        self.assertEqual(hubs['blocks']['blocks'][0]['entity_id'], '1')
        self.assertEqual(json.loads(self.cli('discover'))['blocks']['blocks'][0]['entity_id'], '2')
        source = self.directory / 'source.cs'
        source.write_text('public void Main() { Echo("hello"); }')
        backups = self.directory / 'backups'
        deployed = json.loads(self.cli('deploy', '2', str(source), '--backup-dir', str(backups)))
        self.assertEqual(deployed['mutation_outcome'], 'APPLIED')
        self.assertNotIn('main_invoked', deployed['status'])
        self.assertEqual(next(backups.glob('*.cs')).read_text(), 'old-source')
        output = self.directory / 'read.cs'
        self.cli('read', '2', '--output', str(output))
        self.assertEqual(output.read_text(), source.read_text())
        self.assertTrue(json.loads(self.cli('run', '2', 'hello'))['status']['main_invoked'])

    def test_autopilot_flags_and_route_upload(self):
        self.assertEqual(json.loads(self.cli('controls'))['blocks']['blocks'][0]['entity_id'], '3')
        waypoints = self.directory / 'waypoints.json'
        waypoints.write_text('[{"name":"Home", "x":100, "y":200, "z":300}]')
        self.cli('waypoints', '3', str(waypoints), '--speed', '25')
        state = json.loads(self.cli('autopilot', '3', 'start', '--on-link-loss', 'stop'))['control_status']
        self.assertTrue(state['autopilot_enabled'] and state['stop_on_link_loss'])
        state = json.loads(self.cli('autopilot', '3', 'start', '--on-link-loss', 'continue'))['control_status']
        self.assertTrue(state['autopilot_enabled'])
        self.assertNotIn('stop_on_link_loss', state)
        self.cli('autopilot', '3', 'stop')


if __name__ == '__main__':
    unittest.main()
