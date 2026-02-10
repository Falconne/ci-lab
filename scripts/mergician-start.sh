#!/bin/bash

set -eou pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

pushd "$SCRIPT_DIR/.." >/dev/null

docker compose -f merigcian-compose.yaml down -v
docker compose -f merigcian-compose.yaml up

popd