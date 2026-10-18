#!/usr/bin/env bash
set -euo pipefail
umask 077
cd "$(dirname "$0")/.."
mkdir -p .tools
if [[ ! -f .tools/pulsar/Legacy.exe ]]; then
  curl --fail --location --output .tools/Pulsar-v2.4.3-win-x64.zip https://github.com/SpaceGT/Pulsar/releases/download/v2.4.3/Pulsar-v2.4.3-win-x64.zip
  mkdir -p .tools/pulsar
  unzip -q .tools/Pulsar-v2.4.3-win-x64.zip -d .tools/pulsar
fi
python3 -m venv .tools/venv
.tools/venv/bin/python -m pip install -e client/python
export SPACE_ENGINEERS_BIN64="${SPACE_ENGINEERS_BIN64:-$HOME/.local/share/Steam/steamapps/common/SpaceEngineers/Bin64}"
bash scripts/build.sh
.tools/venv/bin/python scripts/install-local.py "$@"
