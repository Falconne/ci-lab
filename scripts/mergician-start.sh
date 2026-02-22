#!/bin/bash

set -eou pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

DOCKER_DETACH=false

while getopts ":d" opt; do
	case "$opt" in
		d)
			DOCKER_DETACH=true
			;;
		*)
			echo "Usage: $0 [-d]"
			exit 1
			;;
	esac
done

pushd "$SCRIPT_DIR/.." >/dev/null

docker compose -f mergician-compose.yaml down -v

if [ "$DOCKER_DETACH" = true ]; then
	docker compose -f mergician-compose.yaml up -d --build
else
	docker compose -f mergician-compose.yaml up --build
fi

popd