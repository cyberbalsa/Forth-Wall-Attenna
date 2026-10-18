#!/usr/bin/env python3
"""Create a disposable radio-relay test world from the installed Empty World template."""
import argparse
import os
from datetime import datetime, timezone
from pathlib import Path
import shutil
import uuid
import xml.etree.ElementTree as E
from steam_paths import steam_user

XSI = '{http://www.w3.org/2001/XMLSchema-instance}'
E.register_namespace('xsi', XSI[1:-1])


def value(parent, name, text):
    node = parent.find(name)
    if node is None:
        node = E.SubElement(parent, name)
    node.text = str(text)
    return node


def write(tree, path):
    # Templates use xsd in xsi:type values, so retain that namespace declaration.
    tree.getroot().set('xmlns:xsd', 'http://www.w3.org/2001/XMLSchema')
    E.indent(tree)
    tree.write(path, encoding='utf-8', xml_declaration=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--steam-root', type=Path, default=Path.home() / '.local/share/Steam')
    parser.add_argument('--steam-user', help='numeric Steam/userdata account directory; auto-detected when only one exists')
    args = parser.parse_args()
    game = Path(os.environ['SPACE_ENGINEERS_BIN64']).parent if os.environ.get('SPACE_ENGINEERS_BIN64') else args.steam_root / 'steamapps/common/SpaceEngineers'
    data = args.steam_root / 'steamapps/compatdata/244850/pfx/drive_c/users/steamuser/AppData/Roaming/SpaceEngineers'
    try:
        account = steam_user(args.steam_root, args.steam_user)
    except ValueError as error:
        raise SystemExit(str(error)) from None
    steam_id = str(76561197960265728 + int(account))
    name = 'Forth Wall Attenna - Relay Test'
    target = data / 'Saves' / steam_id / name
    if target.exists():
        raise SystemExit('Test world already exists; it was left unchanged: ' + str(target))
    shutil.copytree(game / 'Content/CustomWorlds/Empty World', target)
    # The XML sector is edited below; an older binary sector would take precedence.
    for binary in target.glob('*.sbsB5'):
        binary.unlink()
    checkpoint = E.parse(target / 'Sandbox.sbc')
    root = checkpoint.getroot()
    player = root.find('AllPlayersData/dictionary/item')
    old_steam = player.findtext('Key/ClientId')
    owner = player.findtext('Value/IdentityId')
    for node in root.iter():
        if node.text == old_steam:
            node.text = steam_id
    value(root, 'WorldId', uuid.uuid4())
    for file, tree in (('Sandbox.sbc', checkpoint), ('Sandbox_config.sbc', E.parse(target / 'Sandbox_config.sbc'))):
        r = tree.getroot()
        value(r, 'SessionName', name)
        value(r, 'LastSaveTime', datetime.now(timezone.utc).isoformat())
        settings = r.find('Settings')
        for key, setting in {'GameMode':'Creative', 'OnlineMode':'OFFLINE', 'EnableIngameScripts':'true',
                             'ExperimentalMode':'true', 'EnableResearch':'false', 'ProceduralDensity':'0',
                             'AutoSaveInMinutes':'0', 'EnableDrones':'false', 'EnableEncounters':'false',
                             'EnableCargoShips':'false', 'EnableSpectator':'true'}.items():
            value(settings, key, setting)
        mods = r.find('Mods')
        mods.clear()
        item = E.SubElement(mods, 'ModItem', FriendlyName='Forth Wall Attenna')
        value(item, 'Name', 'ForthWallAttenna')
        value(item, 'PublishedFileId', 0)
        value(item, 'PublishedServiceName', 'Steam')
        write(tree, target / file)
    sector = E.parse(target / 'SANDBOX_0_0_0_.sbs')
    objects = sector.getroot().find('SectorObjects')
    char = objects[0]
    pose = char.find('PositionAndOrientation')
    pose.find('Position').attrib.update(x='0', y='12', z='0')
    pose.find('Forward').attrib.update(x='0', y='0', z='-1')
    pose.find('Up').attrib.update(x='0', y='1', z='0')
    orientation = pose.find('Orientation')
    if orientation is not None:
        pose.remove(orientation)
    value(char, 'JetpackEnabled', 'true')
    counter = 130000000000000000

    def next_id():
        nonlocal counter
        counter += 1
        return counter

    def block(blocks, kind, subtype, x, y, z, name='', forward='Forward', up='Up', **properties):
        b = E.SubElement(blocks, 'MyObjectBuilder_CubeBlock', {XSI+'type':'MyObjectBuilder_'+kind})
        value(b, 'SubtypeName', subtype)
        value(b, 'EntityId', next_id())
        E.SubElement(b, 'Min', x=str(x), y=str(y), z=str(z))
        E.SubElement(b, 'BlockOrientation', Forward=forward, Up=up)
        value(b, 'Owner', owner)
        value(b, 'BuiltBy', owner)
        value(b, 'ShareMode', 'None')
        if name:
            value(b, 'CustomName', name)
        for key, data in properties.items():
            value(b, key, data)
        return b

    for grid_name, z, hub, remote in [('Hub Station', -30, True, False), ('Antenna Relay (no PB)', -2030, False, False), ('Remote Test Ship', -4030, False, True)]:
        grid = E.SubElement(objects, 'MyObjectBuilder_EntityBase', {XSI+'type':'MyObjectBuilder_CubeGrid'})
        value(grid, 'SubtypeName', '')
        value(grid, 'EntityId', next_id())
        value(grid, 'PersistentFlags', 'CastShadows InScene')
        pos = E.SubElement(grid, 'PositionAndOrientation')
        E.SubElement(pos, 'Position', x='0', y='0', z=str(z))
        E.SubElement(pos, 'Forward', x='0', y='0', z='-1')
        E.SubElement(pos, 'Up', x='0', y='1', z='0')
        value(grid, 'GridSizeEnum', 'Large')
        blocks = E.SubElement(grid, 'CubeBlocks')
        for x in range(8):
            for floor_z in range(5):
                block(blocks, 'CubeBlock', 'LargeBlockArmorBlock', x, 0, floor_z)
        block(blocks, 'BatteryBlock', 'LargeBlockBatteryBlock', 1, 1, 0, 'Test Battery', Enabled='true', CurrentStoredPower='3', ProducerEnabled='true')
        block(blocks, 'RadioAntenna', 'LargeBlockRadioAntenna', 2, 1, 0, 'Relay Radio - 2500 m', Enabled='true', BroadcastRadius='2500', EnableBroadcasting='true')
        if hub or remote:
            block(blocks, 'ProgrammableBlock', 'ForthWallAttennaHubLarge' if hub else 'LargeProgrammableBlock', 0, 1, 0,
                  'Forth Wall Hub' if hub else 'Remote Demo PB', Enabled='true')
        if remote:
            block(blocks, 'RemoteControl', 'LargeBlockRemoteControl', 3, 1, 0, 'Demo Remote Control', ControlThrusters='true', DampenersOverride='true')
            block(blocks, 'Gyro', 'LargeBlockGyro', 4, 1, 0, 'Test Gyro', Enabled='true')
            for x, y, thrust_z, forward, up in [(0,1,2,'Forward','Up'),(2,1,2,'Backward','Up'),(4,1,2,'Left','Up'),
                                               (4,1,3,'Right','Up'),(6,1,0,'Up','Forward'),(6,1,2,'Down','Backward')]:
                block(blocks, 'Thrust', 'LargeBlockSmallThrust', x, y, thrust_z, 'Test Ion Thruster', forward, up, Enabled='true')
        value(grid, 'IsStatic', 'false' if remote else 'true')
        value(grid, 'DisplayName', grid_name)
        value(grid, 'DestructibleBlocks', 'true')
        value(grid, 'DampenersEnabled', 'true')
    write(sector, target / 'SANDBOX_0_0_0_.sbs')
    print('Created:', target)
    print('Hub and target are 4 km apart. A powered relay halfway connects their 2.5 km radios.')


if __name__ == '__main__':
    main()
