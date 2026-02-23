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

# Export the calling user's UID/GID so containers can run as the same user.
# This prevents files created by containers in mounted volumes from being
# owned by root on the host (avoids permission issues when editing files).
export LOCAL_UID="$(id -u)"
export LOCAL_GID="$(id -g)"

# Capture the host Docker socket group so we can add the container to that
# group. This allows containers that need access to /var/run/docker.sock to
# communicate with the host Docker daemon without running as root.
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