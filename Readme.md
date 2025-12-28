# Purpose
This repo contains tools for creating a mock CI/CD environment using Gitlab for source control and TeamCity for CI/CD. It also includes tools for creating dummy data in both services. The purpose is to be able to spin up test infrastructure in a docker based lab environment, so an organisation that uses Gitlab+TeamCity for CI/CD and have a lot of internally developed tools that interact with these servers via REST API, can test updates against a representative sandbox environment, instead of their production CI/CD.

# Quick Start

1. **Configure credentials** (optional - defaults will work):
   ```bash
   cp .env .env.local  # Optional: customize settings
   # Edit .env to set GITLAB_ROOT_PASSWORD (default: changeme123)
   ```

2. **Start the lab**:
   ```bash
   docker compose up
   ```

3. **Access services**:
   - GitLab: http://localhost:8081 (root / changeme123)
   - TeamCity: http://localhost:8111

The bootstrap script runs automatically and will:
- Wait for both services to become available (GitLab can take 2-3 minutes)
- Automatically generate API tokens using the root credentials
- Create sample projects in both services

**Minimal user interaction required!** Just set `GITLAB_ROOT_PASSWORD` in `.env` and run `docker compose up`.

# Directory Structure
The root of the repo contains a docker compose file and related settings for spinning up Gitlab and TeamCity, sharing the same network and exposing necessary UI and API ports to the host. The data for each service is internal to the container and not persisted to disk, so each time the services are recreated we will have a clean start for testing.

The compose file will execute the `scripts/bootstrap.py` script upon creation of services to initialise them with data (see below).

## scripts
This subdirectory contains python scripts for initialising Gitlab and TeamCity with a known set of repos and CIs. The bootstrap script automatically:
- Generates GitLab Personal Access Tokens using root credentials
- Extracts TeamCity authentication tokens
- Creates sample projects in both services

## data
This subdirectory contains data files and sample repos to be used by the scripts for initialising data on the services.

# Manual Token Configuration (Optional)

If automatic token generation fails, you can manually create tokens:

1. **GitLab**: Visit http://localhost:8081/-/profile/personal_access_tokens
   - Create a token with `api`, `read_api`, `write_repository` scopes
   - Set `GITLAB_TOKEN` in `.env`

2. **TeamCity**: Configure authentication in the UI at http://localhost:8111
   - Create an access token
   - Set `TEAMCITY_TOKEN` in `.env`