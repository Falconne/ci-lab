#!/usr/bin/env bash

# Check if Playwright dependencies are available on the host.
# Returns 0 if all required libraries are found, 1 otherwise.
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

# Checks if a suitable dotnet SDK (>= 9.0) is available and if Playwright content is installed.
# Returns 0 (true) if native execution is possible, 1 (false) otherwise.
check_native_execution_possible() {
  if command -v dotnet >/dev/null 2>&1; then
    dotnet_version="$(dotnet --version 2>/dev/null || true)"
    if [[ "$dotnet_version" =~ ^([0-9]+) ]]; then
      major="${BASH_REMATCH[1]}"
      if [ "$major" -ge 9 ]; then
        if check_playwright_deps; then
          return 0
        fi
      fi
    fi
  fi
  return 1
}
