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
  dotnet run --project ./Bootstrap.csproj
else
  echo "Local dotnet not found — running within .NET SDK Docker image..."

  # Pass GITLAB_ROOT_PASSWORD to container for auto-token generation
  GITLAB_ROOT_PASSWORD="${GITLAB_ROOT_PASSWORD:-changeme123}"

  docker run --net=host --rm -it \
    -v "$ROOT_DIR":/workspace \
    -w /workspace/src/Bootstrap \
    -e GITLAB_ROOT_PASSWORD="$GITLAB_ROOT_PASSWORD" \
    -e GITLAB_URL="${GITLAB_URL:-http://localhost:8081}" \
    -e TEAMCITY_URL="${TEAMCITY_URL:-http://localhost:8111}" \
    mcr.microsoft.com/dotnet/sdk:9.0 \
    dotnet run --project /workspace/src/Bootstrap/Bootstrap.csproj
fi
