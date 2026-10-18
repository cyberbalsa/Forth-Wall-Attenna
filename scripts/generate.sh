#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p src/ForthWall.Core/Generated client/python/forthwall
protoc -I protocol --csharp_out=src/ForthWall.Core/Generated --python_out=client/python/forthwall protocol/forthwall.proto
