#!/usr/bin/env bash
set -euo pipefail

# Helper to run the bootstrapper C# app
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$ROOT_DIR"

# Run from the project directory so the bootstrapper finds ../../.env as expected
cd src/Bootstrap

# Pass environment variables to container
GITLAB_ROOT_PASSWORD="${GITLAB_ROOT_PASSWORD:-changeme123}"

# Build bootstrap image if it doesn't exist or Dockerfile changed
if [ ! "$(docker images -q ci-lab-bootstrap:latest 2> /dev/null)" ] || [ "$ROOT_DIR/Dockerfile.bootstrap" -nt "$ROOT_DIR/.docker-bootstrap-build" ]; then
  echo "Building bootstrap Docker image with Playwright support..."
  docker build -t ci-lab-bootstrap:latest -f "$ROOT_DIR/Dockerfile.bootstrap" "$ROOT_DIR"
  touch "$ROOT_DIR/.docker-bootstrap-build"
fi

docker run --net=host --rm -it \
  -v "$ROOT_DIR":/workspace \
  -v "$ROOT_DIR/data/logs":/workspace/data/logs \
  -e GITLAB_ROOT_PASSWORD="$GITLAB_ROOT_PASSWORD" \
  -e GITLAB_URL="${GITLAB_URL:-http://localhost:8081}" \
  -e TEAMCITY_URL="${TEAMCITY_URL:-http://localhost:8111}" \
  ci-lab-bootstrap:latest
