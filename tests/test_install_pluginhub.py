"""Check Plugin Hub setup in a temporary Steam tree, without touching the game."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
spec = importlib.util.spec_from_file_location('install_local', ROOT / 'scripts/install-local.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class PluginHubInstallTests(unittest.TestCase):
    def test_companion_install_preserves_plugin_profile_options_and_token(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'checkout'
            shutil.copytree(ROOT / 'mod', root / 'mod')
            steam = Path(directory) / 'Steam'
            data = steam / 'steamapps/compatdata/244850/pfx/drive_c/users/steamuser/AppData/Roaming/SpaceEngineers'
            data.mkdir(parents=True)
            # Existing setup must survive even when no build or loader is present.
            untouched = {
                root / '.tools/pulsar/Legacy/Profiles/Current.xml': b'<Profile><Local /></Profile>',
                root / '.tools/pulsar/Legacy/Local/ForthWall.Plugin/plugin.dll': b'existing plugin',
                steam / 'userdata/123/config/localconfig.vdf': b'existing launch options',
            }
            for path, content in untouched.items():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
            arguments = ['install-local.py', '--pluginhub', '--steam-root', str(steam)]
            output = io.StringIO()
            with patch.object(installer, 'ROOT', root), patch.object(installer, 'check_game_closed'), \
                    patch.object(sys, 'argv', arguments), contextlib.redirect_stdout(output):
                installer.main()
                token = data / 'Storage/ForthWallAttenna/gateway.token'
                original_token = token.read_bytes()
                # A second install must retain authentication and back up the mod.
                token.chmod(0o644)
                installer.main()
            self.assertEqual(token.read_bytes(), original_token)
            self.assertEqual(len(original_token), 64)
            self.assertNotIn(original_token.decode(), output.getvalue())
            self.assertEqual(token.stat().st_mode & 0o777, 0o600)
            self.assertEqual(token.parent.stat().st_mode & 0o777, 0o700)
            definition = 'Data/CubeBlocks.sbc'
            self.assertEqual((data / 'Mods/ForthWallAttenna' / definition).read_bytes(),
                             (ROOT / 'mod' / definition).read_bytes())
            for path, content in untouched.items():
                self.assertEqual(path.read_bytes(), content)
            backups = sorted((root / 'artifacts/install-backups').iterdir())
            self.assertEqual(len(backups), 2)
            self.assertTrue((backups[1] / 'mod' / definition).is_file())
            manifest = json.loads((backups[1] / 'manifest.json').read_text())
            self.assertIsNone(manifest['plugin'])
            self.assertIsNone(manifest['pulsar'])
            self.assertFalse((root / 'src').exists())

    def test_pluginhub_rejects_launch_option_changes_before_installing(self):
        with patch.object(sys, 'argv', ['install-local.py', '--pluginhub', '--set-launch-options']), \
                patch.object(installer, 'check_game_closed') as check, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                installer.main()
        self.assertEqual(error.exception.code, 2)
        check.assert_not_called()
