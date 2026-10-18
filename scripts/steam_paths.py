"""Resolve a local Steam account without assuming the developer's account ID."""
from pathlib import Path


def steam_user(steam_root: Path, requested: str | None = None) -> str:
    userdata = steam_root / 'userdata'

    def valid(value):
        return value.isascii() and value.isdecimal() and 0 < int(value) < 2**32

    if requested is not None:
        value = str(requested)
        if not valid(value) or not (userdata / value / 'config/localconfig.vdf').is_file():
            raise ValueError('Use --steam-user with a numeric account directory under Steam/userdata containing config/localconfig.vdf.')
        return value
    accounts = sorted(p.name for p in userdata.iterdir()
                      if valid(p.name) and (p / 'config/localconfig.vdf').is_file()) if userdata.is_dir() else []
    if len(accounts) == 1:
        return accounts[0]
    if not accounts:
        raise ValueError('No Steam account configuration found. Launch Steam once or check --steam-root.')
    raise ValueError('Multiple Steam accounts found (' + ', '.join(accounts) + '). Select one with --steam-user ACCOUNT_ID.')
