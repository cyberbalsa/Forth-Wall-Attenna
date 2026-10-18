#!/usr/bin/env python3
"""Install local components while the game is closed. Close Steam to edit launch options."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import xml.etree.ElementTree as ET

from steam_options import get_options, set_options
from steam_paths import steam_user

ROOT = Path(__file__).resolve().parents[1]
ALLOWED_DLLS = {
    'ForthWall.Plugin.dll', 'ForthWall.Core.dll', 'ForthWall.Transport.dll',
    'Google.Protobuf.dll', 'System.Memory.dll', 'System.Buffers.dll',
    'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll',
}


def check_game_closed():
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            argv = (process / 'cmdline').read_bytes().split(b'\0')
        except OSError:
            continue
        if any(arg.lower().endswith((b'/legacy.exe', b'\\legacy.exe', b'/spaceengineers.exe', b'\\spaceengineers.exe')) for arg in argv):
            raise SystemExit('Close Space Engineers before installing.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--steam-root', type=Path, default=Path.home() / '.local/share/Steam')
    parser.add_argument('--steam-user', help='numeric Steam/userdata account directory; auto-detected when only one exists')
    parser.add_argument('--set-launch-options', action='store_true')
    parser.add_argument('--pluginhub', action='store_true',
                        help='install only the block mod and token; Pulsar manages the plugin')
    args = parser.parse_args()
    if args.pluginhub and args.set_launch_options:
        parser.error('--pluginhub uses your existing Pulsar setup; omit --set-launch-options')
    check_game_closed()
    if args.set_launch_options and subprocess.run(['pgrep', '-x', 'steam'], stdout=subprocess.DEVNULL).returncode == 0:
        raise SystemExit('Close Steam before changing its launch options.')
    source = ROOT / 'src/ForthWall.Plugin/bin/Release/net48'
    pulsar = ROOT / '.tools/pulsar'
    if not args.pluginhub:
        missing = ALLOWED_DLLS - {p.name for p in source.glob('*.dll')}
        if missing:
            raise SystemExit('Build first; missing runtime libraries: ' + ', '.join(sorted(missing)))
        if not (pulsar / 'Legacy.exe').is_file():
            raise SystemExit('Extract official Pulsar v2.4.3 win-x64 into .tools/pulsar first.')
    user_data = args.steam_root / 'steamapps/compatdata/244850/pfx/drive_c/users/steamuser/AppData/Roaming/SpaceEngineers'
    if not user_data.is_dir():
        raise SystemExit('Space Engineers Proton user-data directory not found.')
    launch = '"' + str(ROOT / 'scripts/launch-proton.sh') + '" %command%'
    old = None
    if args.set_launch_options:
        try:
            account = steam_user(args.steam_root, args.steam_user)
        except ValueError as error:
            raise SystemExit(str(error)) from None
        config = args.steam_root / 'userdata' / account / 'config/localconfig.vdf'
        with config.open(newline='') as file:
            original = file.read()
        old = get_options(original)
        if old not in (None, '', launch):
            raise SystemExit('Existing launch options require deliberate merging; no configuration was changed.')
        replacement = set_options(original, launch)
        assert get_options(replacement) == launch
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
    backup = ROOT / 'artifacts/install-backups' / stamp
    backup.mkdir(parents=True, mode=0o700)
    runtime = pulsar / 'Legacy'
    target = runtime / 'Local/ForthWall.Plugin'
    if not args.pluginhub:
        if target.exists():
            shutil.copytree(target, backup / 'plugin')
        target.mkdir(parents=True, exist_ok=True)
        for name in ALLOWED_DLLS:
            shutil.copy2(source / name, target / name)
        profile = runtime / 'Profiles/Current.xml'
        profile.parent.mkdir(parents=True, exist_ok=True)
        if profile.exists():
            shutil.copy2(profile, backup / 'Current.xml')
            document = ET.parse(profile)
        else:
            document = ET.ElementTree(ET.Element('Profile'))
        node = document.getroot()
        for key in ('Name', 'GitHub', 'DevFolder', 'Local', 'Mods'):
            if node.find(key) is None:
                ET.SubElement(node, key)
        node.find('Name').text = 'Current'
        local = node.find('Local')
        if not any(n.text == 'ForthWall.Plugin.dll' for n in local):
            ET.SubElement(local, 'string').text = 'ForthWall.Plugin.dll'
        ET.indent(document)
        document.write(profile, encoding='utf-8', xml_declaration=True)
    mod = user_data / 'Mods/ForthWallAttenna'
    if mod.exists():
        shutil.copytree(mod, backup / 'mod')
    shutil.copytree(ROOT / 'mod', mod, dirs_exist_ok=True)
    storage = user_data / 'Storage/ForthWallAttenna'
    storage.mkdir(parents=True, exist_ok=True, mode=0o700)
    storage.chmod(0o700)
    token = storage / 'gateway.token'
    if not token.exists():
        fd = os.open(token, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as file:
            file.write(secrets.token_hex(32))
    token.chmod(0o600)
    if args.set_launch_options and replacement != original:
        saved = backup / 'localconfig.vdf'
        shutil.copy2(config, saved)
        saved.chmod(0o600)
        temporary = config.with_name('localconfig.forthwall.tmp')
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, config.stat().st_mode & 0o777)
        with os.fdopen(fd, 'w', newline='') as file:
            file.write(replacement)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary, config)
    (backup / 'manifest.json').write_text(json.dumps({'pulsar': None if args.pluginhub else '2.4.3', 'previous_launch_options': old,
       'launch_options': launch if args.set_launch_options else None, 'mod': str(mod),
       'plugin': None if args.pluginhub else str(target)}, indent=2))
    if args.pluginhub:
        print('Installed local block mod and private token file. Enable the plugin in Pulsar.')
    else:
        print('Installed plugin, local block mod, and private token file.')
    print('Steam launch options configured.' if args.set_launch_options else 'Steam launch options unchanged.')
    print('Backup:', backup)
    print('Enable the local ForthWallAttenna mod in a test world with in-game scripts enabled.')


if __name__ == '__main__':
    main()
