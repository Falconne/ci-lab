#!/bin/bash

set -eou pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

WITH_BOOTSTRAP=false

while getopts ":b" opt; do
	case "$opt" in
		b)
			WITH_BOOTSTRAP=true
			;;
		*)
			echo "Usage: $0 [-b]"
			exit 1
			;;
	esac
done

pushd "$SCRIPT_DIR/.." >/dev/null

docker compose -f cilab-compose.yaml down -v

if [ "$WITH_BOOTSTRAP" = true ]; then
	docker compose -f cilab-compose.yaml --profile bootstrap up --build
else
	docker compose -f cilab-compose.yaml up
fi

popd