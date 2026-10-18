#!/usr/bin/env bash
set -euo pipefail
umask 077
root="$(cd "$(dirname "$0")/.." && pwd)"
loader="$root/.tools/pulsar/Legacy.exe"
if [[ ! -f "$loader" || $# -lt 2 ]]; then
  printf '%s\n' 'Pulsar is missing or this wrapper was not invoked with Steam %command%.' >&2
  exit 1
fi
# Insert the Windows loader immediately before the game executable in Proton's
# original argv. Preserve paths with spaces and the original game-path argument.
command_args=("$@")
game_index=-1
for index in "${!command_args[@]}"; do
  if [[ "${command_args[index],,}" == *spaceengineers.exe ]]; then game_index=$index; break; fi
done
if (( game_index < 1 )); then
  printf '%s\n' 'No SpaceEngineers.exe argument found in Steam command.' >&2
  exit 1
fi
exec "${command_args[@]:0:game_index}" "$loader" "${command_args[@]:game_index}" -noUpdate -noStats -noDiscord
