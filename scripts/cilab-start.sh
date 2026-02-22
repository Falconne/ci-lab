#!/bin/bash

set -eou pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

WITH_BOOTSTRAP=false
DOCKER_DETACH=false

while getopts ":bd" opt; do
	case "$opt" in
		b)
			WITH_BOOTSTRAP=true
			;;
		d)
			DOCKER_DETACH=true
			;;
		*)
			echo "Usage: $0 [-b] [-d]"
			exit 1
			;;
	esac
done

pushd "$SCRIPT_DIR/.." >/dev/null

export LOCAL_UID="$(id -u)"
export LOCAL_GID="$(id -g)"
export LOCAL_DOCKER_GID="$(stat -c '%g' /var/run/docker.sock)"

docker compose -f cilab-compose.yaml down -v

if [ "$WITH_BOOTSTRAP" = true ]; then
	if [ "$DOCKER_DETACH" = true ]; then
		docker compose -f cilab-compose.yaml --profile bootstrap up -d --build
	else
		docker compose -f cilab-compose.yaml --profile bootstrap up --build
	fi
else
	if [ "$DOCKER_DETACH" = true ]; then
		docker compose -f cilab-compose.yaml up -d
	else
		docker compose -f cilab-compose.yaml up
	fi
fi

popd