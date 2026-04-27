#!/bin/bash

set -eou pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
DOCKERFILE="$SCRIPT_DIR/../Dockerfile.mergician-prod"

echo "Modifying $DOCKERFILE for Ubuntu 22.04 deployment..."

python3 - "$DOCKERFILE" <<'PYEOF'
import sys

filename = sys.argv[1]
with open(filename, 'r') as f:
    content = f.read()

dotnet_base_image = 'FROM mcr.microsoft.com/dotnet/aspnet:9.0'
ubuntu_base_image = 'FROM ubuntu:22.04'

old_install = 'RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*'
new_install = (
    'RUN apt-get update && \\\n'
    '    apt-get install -y --no-install-recommends ca-certificates wget curl apt-transport-https && \\\n'
    '    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb \\\n'
    '        -O /tmp/packages-microsoft-prod.deb && \\\n'
    '    dpkg -i /tmp/packages-microsoft-prod.deb && \\\n'
    '    rm /tmp/packages-microsoft-prod.deb && \\\n'
    '    apt-get update && \\\n'
    '    apt-get install -y --no-install-recommends aspnetcore-runtime-9.0 && \\\n'
    '    rm -rf /var/lib/apt/lists/*'
)

changed = False

if dotnet_base_image in content:
    content = content.replace(dotnet_base_image, ubuntu_base_image)
    print(f"  Replaced base image: {dotnet_base_image} -> {ubuntu_base_image}")
    changed = True
elif ubuntu_base_image in content:
    print(f"  Base image already set to {ubuntu_base_image}, skipping")
else:
    print(f"ERROR: Could not find base image '{dotnet_base_image}' in {filename}", file=sys.stderr)
    sys.exit(1)

if old_install in content:
    content = content.replace(old_install, new_install)
    print("  Replaced curl-only install with aspnetcore-runtime-9.0 + curl installation via Microsoft APT feed")
    changed = True
elif 'aspnetcore-runtime-9.0' in content:
    print("  .NET runtime install already present, skipping")
else:
    print(f"ERROR: Could not find the curl-only install line in {filename}", file=sys.stderr)
    sys.exit(1)

if changed:
    with open(filename, 'w') as f:
        f.write(content)
    print(f"Successfully updated {filename}")
else:
    print(f"{filename} already up to date, no changes made")
PYEOF

echo "Done."
