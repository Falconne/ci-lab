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
1. Build the .NET backend and run it on **http://localhost:5000**
2. Build the Vue frontend and serve it via Nginx on **http://localhost:3000**

Open **http://localhost:3000** in your browser to see the app.

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

---

# CI Lab

*Documentation for CI Lab / Bootstrapper tooling coming soon.*
