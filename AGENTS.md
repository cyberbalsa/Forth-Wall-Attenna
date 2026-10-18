# Forth Wall Attenna

Space Engineers 1 antenna-connected Programmable Block gateway. Preserve this
project name and directory spelling as requested by the user.

## Scope

- Add small- and large-grid hub blocks based on the vanilla Programmable Block.
- Expose an authenticated Protobuf-over-WebSocket endpoint from a Pulsar plugin.
- Route remote PB discovery, deployment, execution, and RPC through the hub.
- Enforce the game's IGC antenna reachability and block ownership rules.
- Keep game API access on the game thread; bound queues and request lifetimes.
- Support the local CachyOS/Proton installation first.

## Development

Game assemblies are reference-only and must never be committed or redistributed.
Read their location from `SPACE_ENGINEERS_BIN64` or the build property `GameBin`.
Keep tokens out of source, logs, and chat. Local tooling belongs under `.tools/`.
Temporary decompilation output belongs outside the repository.

Default game location:
`~/.local/share/Steam/steamapps/common/SpaceEngineers/Bin64`

Use the .NET 9 SDK on PATH, set `FORTHWALL_DOTNET` to its executable, or place it
under `.tools/dotnet/`. The build script passes the selected SDK to Python tests.

Validate the protocol and lifecycle using standalone tests, compile the plugin
against the installed game, and distinguish those checks from in-game testing.
