#!/usr/bin/env bash
set -euo pipefail

# Helper to run the bootstrapper C# app
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$ROOT_DIR"

# Run from the project directory so the bootstrapper finds ../../../.env as expected
cd src/be/Bootstrap

# Source common helpers
source "$ROOT_DIR/scripts/common.sh"

# Pass environment variables to container
GITLAB_ROOT_PASSWORD="${GITLAB_ROOT_PASSWORD:-changeme123}"

# If a suitable dotnet SDK is available locally, run the bootstrapper natively.
# Otherwise fall back to running inside Docker (existing behaviour).
if check_native_execution_possible; then
  echo "Found dotnet and Playwright dependencies — running bootstrapper natively."
  export GITLAB_ROOT_PASSWORD
  # Run from the project directory so local relative paths match expectations
  cd "$ROOT_DIR/src/be/Bootstrap"
  dotnet restore "$ROOT_DIR/src/be/Bootstrap/Bootstrap.csproj"
  dotnet run --project "$ROOT_DIR/src/be/Bootstrap/Bootstrap.csproj" --configuration Release -- "$@"
  exit $?
else
  echo "Missing dotnet 9 or Playwright dependencies — falling back to Docker."
fi

# Build bootstrap image if it doesn't exist or Dockerfile changed
if [ ! "$(docker images -q ci-lab-bootstrap:latest 2> /dev/null)" ] || [ "$ROOT_DIR/Dockerfile.bootstrap" -nt "$ROOT_DIR/.docker-bootstrap-build" ]; then
  echo "Building bootstrap Docker image with Playwright support..."
  docker build -t ci-lab-bootstrap:latest -f "$ROOT_DIR/Dockerfile.bootstrap" "$ROOT_DIR"
  touch "$ROOT_DIR/.docker-bootstrap-build"
fi

# TODO: If bootstrapper doesn't finish within 15 minutes, something has gone wrong. Make sure these is a time to stop the container and exit with a non-zero
# exit code if this happens. We still need to see the output from the bootstrap in stdout so the agent can't diagnose issues then they happen.

docker run --net=host --rm -it \
  -v "$ROOT_DIR":/workspace \
  -v "$ROOT_DIR/data/logs":/workspace/data/logs \
  -e GITLAB_ROOT_PASSWORD="$GITLAB_ROOT_PASSWORD" \
  -e GITLAB_URL="${GITLAB_URL:-http://localhost:8081}" \
  -e TEAMCITY_URL="${TEAMCITY_URL:-http://localhost:8111}" \
  ci-lab-bootstrap:latest "$@"
