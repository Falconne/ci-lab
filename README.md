# Mergician

Mergician is a web application that helps developers manage "merge groups" — coordinated sets of merge requests across multiple Git repositories that share a common branch name. It provides a web interface to visualise, manage, and orchestrate merges across repos.

## Architecture

- **Backend**: ASP.NET Core 9 Web API (`src/be/Mergician/`)
- **Frontend**: Vue 3 + Vuetify 3 + Vite (`src/fe/`)
- **Deployment**: Docker Compose (`mergician-compose.yaml`)

## Prerequisites

### For Docker-based development

- Docker Engine 24+ with Docker Compose v2
- That's it — all builds happen inside containers.

### For direct local development

| Tool | Minimum Version | Install |
|------|----------------|---------|
| .NET SDK | 9.0 | https://dotnet.microsoft.com/download |
| Node.js | 20.0 | https://nodejs.org or use [nvm](https://github.com/nvm-sh/nvm) |
| npm | 10.0 | Included with Node.js |

> **Tip**: The frontend includes a `.nvmrc` file. If you use [nvm](https://github.com/nvm-sh/nvm), run `nvm use` in the `src/fe/` directory to switch to the correct Node version.
>
> Run `npm run check-env` in `src/fe/` to verify your local environment has the required tools.

## Development
The app can be run for dev testing either using docker compose or with native dev tools if they are installed on the machine.

### Docker-based

From the repository root:

```bash
docker compose -f mergician-compose.yaml up --build
```

This will:
1. Build the .NET backend with embedded frontend
2. Serve both on **http://localhost:5000**

Open **http://localhost:5000** in your browser to see the app.

To stop:

```bash
docker compose -f mergician-compose.yaml down
```

### Direct (no Docker)

#### Backend

```bash
cd src/be/Mergician
dotnet build           # Build
dotnet run             # Run (serves on http://localhost:5000)
```

#### Frontend

```bash
cd src/fe
npm install            # Install dependencies (first time / after package.json changes)
npm run dev            # Start Vite dev server on http://localhost:5173
```

The Vite dev server proxies `/api/*` requests to the backend at `http://localhost:5000`, so both can run simultaneously during development.

#### Development workflow

1. Start the backend: `cd src/be/Mergician && dotnet run`
2. Start the frontend: `cd src/fe && npm run dev`
3. Open **http://localhost:5173** in your browser
4. Changes to Vue files hot-reload automatically; .NET changes require a restart (or use `dotnet watch`).

## Project Structure

```
src/
├── be/                            # .NET backend
│   ├── Mergician.sln
│   ├── Mergician/                 # Mergician web API project
│   │   ├── Controllers/           # API controllers
│   │   ├── Program.cs             # Application entry point
│   │   └── Mergician.csproj
│   └── Bootstrap/                 # CI Lab bootstrapper project
│       ├── Entities/              # API response models
│       ├── Services/              # Bootstrap services
│       ├── Utilities/             # Helpers and extensions
│       ├── Program.cs
│       └── Bootstrap.csproj
└── fe/                            # Vue frontend
    ├── src/
    │   ├── components/        # Reusable Vue components
    │   ├── views/             # Page-level components
    │   ├── router/            # Vue Router configuration
    │   ├── App.vue            # Root component
    │   └── main.ts            # Application entry point
    ├── package.json
    ├── vite.config.ts         # Vite build configuration
    └── tsconfig.json          # TypeScript configuration
```

## Building for Production

The production build produces a single Docker image that bundles both the .NET backend and the Vue frontend. The backend serves the frontend as static files from its `wwwroot/` directory, so no separate web server is needed.

### Build the image

```bash
docker build -t mergician:latest .
```

This multi-stage build:
1. Installs npm dependencies and runs `vite build` for the frontend.
2. Restores NuGet packages and runs `dotnet publish` for the backend.
3. Copies the frontend `dist/` into the backend's `wwwroot/` in a slim ASP.NET runtime image.

### Run locally

```bash
docker run --name mergician -p 5000:5000 mergician:latest
```

Open **http://localhost:5000** to see the app. The health endpoint is at **http://localhost:5000/api/health**.

To stop and remove:

```bash
docker rm -f mergician
```

### Manual builds (without Docker)

If you need to build outside of Docker:

```bash
# Backend
cd src/be/Mergician
dotnet publish Mergician.csproj -c Release -o ./publish

# Frontend
cd src/fe
npm run build

# Combine: copy frontend output into backend publish directory
cp -r src/fe/dist/* src/be/Mergician/publish/wwwroot/
```

## Configuration

Mergician is configured via the standard ASP.NET `appsettings.json` file located at `src/be/Mergician/appsettings.json`. The key settings are under the `Mergician` section:

```json
{
  "Mergician": {
    "GitLab": {
      "Url": "https://gitlab.example.com",
      "InternalUrl": "",
      "ServiceToken": "<personal-access-token-with-api-scope>",
      "OAuth": {
        "ClientId": "<your-oauth-app-id>",
        "ClientSecret": "<your-oauth-app-secret>"
      }
    }
  }
}
```

| Setting | Description | Default |
|---------|-------------|---------|
| `Mergician:GitLab:Url` | Browser-facing GitLab URL (used for OAuth redirects) | _(empty — must be configured)_ |
| `Mergician:GitLab:InternalUrl` | Server-side GitLab URL (for API calls from within Docker). Falls back to `Url` if not set. | _(empty)_ |
| `Mergician:GitLab:ServiceToken` | Personal access token (with `api` scope) for a dedicated service account. Used for background monitoring and performing merge actions on behalf of Mergician. | _(empty)_ |
| `Mergician:GitLab:OAuth:ClientId` | OAuth Application ID registered in GitLab | _(empty)_ |
| `Mergician:GitLab:OAuth:ClientSecret` | OAuth Application Secret | _(empty)_ |

The default `appsettings.json` ships with **empty** values — Mergician requires explicit configuration for the target GitLab server. For CI Lab development, `appsettings.Development.json` provides the `localhost:8081` URL.

> **Note:** When Mergician runs inside a Docker container on a bridge network, it cannot reach GitLab via `localhost`. Set `InternalUrl` to the Docker-resolvable address (e.g. `http://gitlab:8081`). The `Url` remains the browser-accessible address. When running natively (no Docker), leave `InternalUrl` empty and `Url` is used for everything.

### Configuration methods

1. **Environment variables** (recommended for Docker): set `Mergician__GitLab__Url`, `Mergician__GitLab__InternalUrl`, `Mergician__GitLab__ServiceToken`, `Mergician__GitLab__OAuth__ClientId`, and `Mergician__GitLab__OAuth__ClientSecret`.
2. **Edit `appsettings.json`** directly.
3. **Use `appsettings.Production.json`** to override only the production values.

To register Mergician as a GitLab OAuth application on your production server:

1. Log in to GitLab as an Admin.
2. Go to **Admin Area > Applications** (or the group/user-level Applications page).
3. Create a new application with:
   - **Name**: Mergician
   - **Redirect URI**: `http://<mergician-host>:5000/api/auth/callback`
   - **Scopes**: `read_user`, `read_api`
   - **Confidential**: Yes
4. Copy the **Application ID** and **Secret** into the settings above.

### Service token for background operations

Mergician uses a dedicated GitLab personal access token for background monitoring and performing merge actions. To set this up:

1. Create a dedicated service account in GitLab (e.g. a "bot" user).
2. As an admin, go to **Admin Area > Users**, find the service account, and create a **Personal Access Token** with the `api` scope.
3. Set the token as `Mergician:GitLab:ServiceToken` in your configuration or via the `Mergician__GitLab__ServiceToken` environment variable.

> **Note:** In the CI Lab environment, the bootstrapper automatically creates this token for the `b.builder` account and writes it to `.env` as `GITLAB_SERVICE_TOKEN`.

---

# CI Lab

CI Lab provides a local integration testing environment for Mergician, using GitLab (Omnibus) and TeamCity spun up via Docker Compose, plus an automated C# bootstrapper that creates test accounts, projects, and OAuth applications.

## Prerequisites

- Docker Engine 24+ with Docker Compose v2
- Ports 8081 (GitLab), 8111 (TeamCity), and 5000 (Mergician) available

## Starting the CI Lab environment

```bash
./scripts/cilab-start.sh
```

This tears down any previous session, cleans stale tokens from `.env`, and starts the CI Lab containers. GitLab takes 3–5 minutes to become healthy on first start.

## Running the Bootstrapper

Once the CI Lab containers are running and healthy:

```bash
./scripts/bootstrap.sh
```

The bootstrapper creates test users, sample GitLab projects, registers the Mergician OAuth application, and writes credentials to `.env`. Use a timeout for CI scenarios:

```bash
timeout 120 ./scripts/bootstrap.sh || true
```

## Starting Mergician against CI Lab

```bash
./scripts/mergician-start.sh
```

Or manually:

```bash
docker compose -f mergician-compose.yaml up --build
```

Mergician will be accessible at `http://localhost:5000`. It reads OAuth credentials from `.env` (written by the bootstrapper).

## Integration Tests

The `IntegrationTest` project (`src/be/IntegrationTest/`) contains Playwright-based end-to-end tests that exercise Mergician against the CI Lab environment.

### What is tested

- **Authentication**: Full OAuth login flow — navigates to Mergician's login endpoint, authenticates as `test1` on GitLab, authorizes the OAuth app, and verifies the `/api/auth/me` endpoint returns the logged-in user.
- **Activity**: Creates a personal access token for `test1`, pushes a test commit to a GitLab project, logs into Mergician, and verifies the activity stream shows the git event.

### Prerequisites

Both CI Lab and Mergician must be running:

```bash
# 1. Start CI Lab (if not already running)
./scripts/cilab-start.sh

# 2. Run the bootstrapper (if not already done)
./scripts/bootstrap.sh

# 3. Start Mergician
docker compose -f mergician-compose.yaml up --build -d
```

### Running the tests

From the repository root with .NET 9 SDK installed:

```bash
cd src/be/IntegrationTest
dotnet run
```

The tests use Playwright in headless mode. On first run, install the browsers:

```bash
# From the IntegrationTest project directory
dotnet build
pwsh bin/Debug/net9.0/playwright.ps1 install chromium
```

Or if PowerShell is not available:

```bash
npx playwright install chromium
```

### Test output

- **Logs**: `data/logs/integration-test.log`
- **Screenshots**: `data/screenshots/integration-test/auth/` and `data/screenshots/integration-test/activity/` — captured at each step for debugging.
- **Exit code**: `0` if all tests pass, `1` if any fail.

### Test configuration

Test settings are in `src/be/IntegrationTest/TestConfig.cs`:

| Setting | Value | Description |
|---------|-------|-------------|
| `GitLabUrl` | `http://localhost:8081` | CI Lab GitLab instance |
| `MergicianUrl` | `http://localhost:5000` | Mergician instance |
| `TestUsername` | `test1` | Test account created by bootstrapper |
| `TestPassword` | `changeme123` | Test account password |
