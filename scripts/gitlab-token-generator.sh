#!/bin/sh
set -euo pipefail

# Script extracted from docker-compose gitlab-token-generator service
# Logs are redirected to /workspace/data/logs/gitlab-token-generator.log

mkdir -p /workspace/data/logs
exec > /workspace/data/logs/gitlab-token-generator.log 2>&1
apk add --no-cache docker-cli curl

echo "[token-gen] Waiting for GitLab container..."
CONTAINER=$(docker ps --filter "name=gitlab-1" --format "{{.Names}}" | head -n1)
if [ -z "$CONTAINER" ]; then
  echo "[token-gen] ERROR: GitLab container not found"
  exit 1
fi
echo "[token-gen] Found GitLab container: $CONTAINER"
echo "[token-gen] Creating Personal Access Token for root user..."
TOKEN=$(docker exec $CONTAINER /opt/gitlab/bin/gitlab-rails runner "user = User.find_by_username('root'); token = user.personal_access_tokens.create(scopes: ['api', 'read_api', 'write_repository'], name: 'bootstrap-automation', expires_at: 365.days.from_now); puts token.token" 2>/dev/null | tail -n1)
if [ -n "$TOKEN" ] && [ ${#TOKEN} -gt 10 ]; then
  echo "[token-gen] Token created successfully: $TOKEN"

  # Validate token before writing to .env (retry up to 30 seconds)
  echo "[token-gen] Validating token..."
  MAX_RETRIES=6
  RETRY_DELAY=5
  VALIDATED=0
  i=1
  while [ $i -le $MAX_RETRIES ]; do
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "PRIVATE-TOKEN: $TOKEN" http://gitlab:8081/api/v4/user)
    if [ "$HTTP_CODE" = "200" ]; then
      echo "[token-gen] Token validated successfully (HTTP $HTTP_CODE)"
      VALIDATED=1
      break
    else
      echo "[token-gen] Token validation returned HTTP $HTTP_CODE (attempt $i/$MAX_RETRIES)"
      if [ $i -lt $MAX_RETRIES ]; then
        sleep $RETRY_DELAY
      fi
    fi
    i=$((i+1))
  done

  if [ $VALIDATED -eq 0 ]; then
    echo "[token-gen] ERROR: Token validation failed after $MAX_RETRIES attempts"
    exit 1
  fi

  if grep -q "^GITLAB_TOKEN=" .env 2>/dev/null; then
    sed -i "s|^GITLAB_TOKEN=.*|GITLAB_TOKEN=\"$TOKEN\"|" .env
    echo "[token-gen] Updated GITLAB_TOKEN in .env"
  else
    echo "GITLAB_TOKEN=\"$TOKEN\"" >> .env
    echo "[token-gen] Added GITLAB_TOKEN to .env"
  fi
else
  echo "[token-gen] ERROR: Failed to create token"
  exit 1
fi
