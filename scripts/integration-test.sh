#!/usr/bin/env bash
set -euo pipefail

# Helper to run the integration tests
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$ROOT_DIR"

# Source common helpers
source "$ROOT_DIR/scripts/common.sh"

# Restart Mergician with a fresh database. Accumulated branch records from previous
# test runs slow down the background sync cycle, making timing-sensitive tests flaky.
echo "Restarting Mergician with a fresh database..."
docker compose -f mergician-compose.yaml down -v --timeout 15
docker compose -f mergician-compose.yaml up -d --build

echo "Waiting for Mergician to be healthy..."
for i in $(seq 1 45); do
  if curl -sf http://localhost:5000/api/health > /dev/null 2>&1; then
    echo "Mergician is healthy."
    break
  fi
  if [ "$i" -eq 45 ]; then
    echo "ERROR: Mergician did not become healthy within 90 seconds."
    exit 1
  fi
  sleep 2
done

# Run from the project directory
cd src/be/IntegrationTest

# Pass environment variables
GITLAB_ROOT_PASSWORD="${GITLAB_ROOT_PASSWORD:-changeme123}"

# If a suitable dotnet SDK is available locally, run natively.
if check_native_execution_possible; then
  echo "Found dotnet and Playwright dependencies — running integration tests natively."
  export GITLAB_ROOT_PASSWORD
  cd "$ROOT_DIR/src/be/IntegrationTest"
  dotnet restore "$ROOT_DIR/src/be/IntegrationTest/IntegrationTest.csproj"
  dotnet run --project "$ROOT_DIR/src/be/IntegrationTest/IntegrationTest.csproj" --configuration Release
  exit $?
else
  echo "Missing dotnet 9 or Playwright dependencies — falling back to Docker."
fi

# Build integration-test image if it doesn't exist or Dockerfile changed
if [ ! "$(docker images -q ci-lab-integration-test:latest 2> /dev/null)" ] || [ "$ROOT_DIR/Dockerfile.integration-test" -nt "$ROOT_DIR/.docker-integration-test-build" ]; then
  echo "Building integration-test Docker image with Playwright support..."
  docker build -t ci-lab-integration-test:latest -f "$ROOT_DIR/Dockerfile.integration-test" "$ROOT_DIR"
  touch "$ROOT_DIR/.docker-integration-test-build"
fi

docker run --net=host --rm -it \
  -v "$ROOT_DIR":/workspace \
  -v "$ROOT_DIR/data/logs":/workspace/data/logs \
  -v "$ROOT_DIR/data/screenshots":/workspace/data/screenshots \
  -e GITLAB_ROOT_PASSWORD="$GITLAB_ROOT_PASSWORD" \
  -e GITLAB_URL="${GITLAB_URL:-http://localhost:8081}" \
  -e TEAMCITY_URL="${TEAMCITY_URL:-http://localhost:8111}" \
  ci-lab-integration-test:latest
