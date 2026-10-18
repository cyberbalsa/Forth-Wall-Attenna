#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
sdk="${FORTHWALL_DOTNET:-dotnet}"
if { ! command -v "$sdk" >/dev/null 2>&1 || [[ -z "$("$sdk" --list-sdks 2>/dev/null)" ]]; } && [[ -x .tools/dotnet/dotnet ]]; then
  sdk="$PWD/.tools/dotnet/dotnet"
fi
if ! command -v "$sdk" >/dev/null 2>&1 || [[ -z "$("$sdk" --list-sdks 2>/dev/null)" ]]; then
  echo 'Install the .NET 9 SDK or set FORTHWALL_DOTNET to its dotnet executable.' >&2
  exit 1
fi
export FORTHWALL_DOTNET="$sdk"
bash scripts/generate.sh
"$sdk" build src/ForthWall.Plugin/ForthWall.Plugin.csproj -c Release "$@"
"$sdk" run --project tests/ForthWall.Tests -c Release
if [[ -x .tools/venv/bin/python ]]; then
  .tools/venv/bin/python -m unittest discover -s tests -p 'test_*.py' -v
fi
