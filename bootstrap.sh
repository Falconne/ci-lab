#!/usr/bin/env bash
set -euo pipefail

# Helper to run the bootstrapper (prefers local dotnet; falls back to Docker SDK image)
ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"

cd "$ROOT_DIR"

if [ ! -d "src/Bootstrap" ]; then
  echo "Error: src/Bootstrap directory not found. Run from repository root." >&2
  exit 2
fi

# Run from the project directory so the bootstrapper finds ../../.env as expected
cd src/Bootstrap

if command -v dotnet >/dev/null 2>&1; then
  echo "Running bootstrapper with local dotnet..."

  # Ensure Playwright browsers are installed
  if [ ! -d "$HOME/.cache/ms-playwright" ]; then
    echo "Installing Playwright browsers..."
    dotnet tool install --global Microsoft.Playwright.CLI 2>/dev/null || true
    export PATH="$PATH:$HOME/.dotnet/tools"
    playwright install chromium 2>/dev/null || true
  fi

  dotnet run --project ./Bootstrap.csproj
else
  echo "Local dotnet not found — running within .NET SDK Docker image with Playwright..."

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
fi
