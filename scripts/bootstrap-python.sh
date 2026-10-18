#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
if [[ ! -x "$root/.tools/venv/bin/python" ]]; then
  python3 -m venv "$root/.tools/venv"
fi
"$root/.tools/venv/bin/python" -m pip install --disable-pip-version-check -e "$root/client/python"
echo 'Python library ready. Run scripts/forthwall-dashboard or .tools/venv/bin/python examples/python/getting_started.py.'
