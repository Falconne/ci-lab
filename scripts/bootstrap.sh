#!/usr/bin/env bash
set -euo pipefail

# Helper to run the bootstrapper C# app
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$ROOT_DIR"

# Run from the project directory so the bootstrapper finds ../../../.env as expected
cd src/be/Bootstrap

# Pass environment variables to container
GITLAB_ROOT_PASSWORD="${GITLAB_ROOT_PASSWORD:-changeme123}"

# Check if Playwright dependencies are available on the host.
# Returns 0 if all required libraries are found, 1 otherwise.
#
# Note: ldconfig output is cached in a variable first to avoid a pipefail
# issue where grep -q exits early after finding a match, causing ldconfig to
# receive SIGPIPE and return a non-zero exit code. With pipefail enabled, this
# would make the entire pipeline fail even though the library was found.
check_playwright_deps() {
  local cache
  cache="$(ldconfig -p 2>/dev/null || true)"

  # List of critical shared libraries required by Playwright/Chromium
  local libs=(
    "libnss3.so"
    "libnspr4.so"
    "libatk-1.0.so"
    "libatk-bridge-2.0.so"
    "libcups.so"
    "libdrm.so"
    "libxkbcommon.so"
    "libgbm.so"
    "libasound.so"
    "libpango-1.0.so"
    "libcairo.so"
  )

  for lib in "${libs[@]}"; do
    if ! grep -q "$lib" <<< "$cache"; then
      return 1
    fi
  done
  return 0
}

# If a suitable dotnet SDK is available locally, run the bootstrapper natively.
# Otherwise fall back to running inside Docker (existing behaviour).
if command -v dotnet >/dev/null 2>&1; then
  dotnet_version="$(dotnet --version 2>/dev/null || true)"
  if [[ "$dotnet_version" =~ ^([0-9]+) ]]; then
    major="${BASH_REMATCH[1]}"
    if [ "$major" -ge 9 ]; then
      if check_playwright_deps; then
        echo "Found dotnet $dotnet_version and Playwright dependencies — running bootstrapper natively."
        export GITLAB_ROOT_PASSWORD
        # Run from the project directory so local relative paths match expectations
        cd "$ROOT_DIR/src/be/Bootstrap"
        dotnet restore "$ROOT_DIR/src/be/Bootstrap/Bootstrap.csproj"
        dotnet run --project "$ROOT_DIR/src/be/Bootstrap/Bootstrap.csproj" --configuration Release
        exit $?
      else
        echo "Found dotnet $dotnet_version but Playwright dependencies are missing — falling back to Docker."
      fi
    fi
  fi
fi

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
