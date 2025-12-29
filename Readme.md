# Purpose
This repo contains tools for creating a mock CI/CD environment using Gitlab for source control and TeamCity for CI/CD. It also includes tools for creating dummy data in both services. The purpose is to be able to spin up test infrastructure in a docker based lab environment, so an organisation that uses Gitlab+TeamCity for CI/CD and have a lot of internally developed tools that interact with these servers via REST API, can test updates against a representative sandbox environment, instead of their production CI/CD.

# Quick Start

1. **Start the lab services**:
   ```bash
   docker compose up -d
   ```

2. **Access services and create tokens**:
   - GitLab: http://localhost:8081 (root / changeme123)
     - Create a Personal Access Token with `api`, `read_api`, `write_repository` scopes at http://localhost:8081/-/profile/personal_access_tokens
   - TeamCity: http://localhost:8111
     - Complete initial setup and create an access token at http://localhost:8111/profile.html?item=accessTokens

3. **Run the bootstrap script**:
   ```bash
   cd src/Bootstrap
   dotnet run
   ```

The bootstrap script will:
- Wait for both services to become available
- Prompt you for GitLab and TeamCity tokens if not already in `.env`
- Validate the tokens with test API calls
- Save valid tokens to `.env` file for future use
- Create sample projects in both services
- Authorize TeamCity agents

**Note**: The bootstrap script checks for tokens in environment variables and the `.env` file. If tokens are invalid or missing, it will prompt you interactively to provide them.

# Directory Structure
The root of the repo contains a docker compose file and related settings for spinning up Gitlab and TeamCity, sharing the same network and exposing necessary UI and API ports to the host. The data for each service is internal to the container and not persisted to disk, so each time the services are recreated we will have a clean start for testing.

## src
This subdirectory contains a .NET 9 C# application for initialising Gitlab and TeamCity with a known set of repos and CIs. The bootstrap application:
- Interactively prompts for and validates API tokens
- Saves tokens to `.env` file for convenience
- Creates sample projects in both services
- Authorizes TeamCity build agents

Built with modern C# patterns including top-level statements, nullable reference types, and HttpClientFactory.

## data
This subdirectory contains data files and sample repos to be used by the scripts for initialising data on the services.

# Token Management

Tokens are stored in the `.env` file at the repository root:
- `GITLAB_TOKEN`: Personal Access Token from GitLab
- `TEAMCITY_TOKEN`: Access Token from TeamCity

The bootstrap script will automatically create or update this file when you provide valid tokens.