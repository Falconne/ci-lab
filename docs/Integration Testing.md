# Integration Testing

The `IntegrationTest` project (`src/be/IntegrationTest/`) runs Playwright-based end-to-end tests against a live Mergician instance backed by the CI Lab environment.

## Prerequisites

- **CI Lab running**: Docker containers from `cilab-compose.yaml` must be up and bootstrapped.
- **Mergician running**: Either via `mergician-compose.yaml` or natively (`dotnet run` in `src/be/Mergician`).
- The GitLab OAuth app must be registered (done automatically by the bootstrapper).

## Running

```bash
cd src/be/IntegrationTest
dotnet run
```

Exit code `0` = all tests passed; `1` = at least one test failed.

## Project Structure

```
IntegrationTest/
├── Program.cs          # Test runner — instantiates and runs each test
├── TestConfig.cs       # URLs, credentials, paths (hardcoded to CI Lab defaults)
└── Tests/
    ├── AuthenticationTest.cs   # OAuth login flow via Playwright
    └── ActivityTest.cs         # Git push + activity stream verification
```

## Shared PlaywrightService

Browser automation is provided by the `PlaywrightService` class library (`src/be/PlaywrightService/`). It contains:

- `BrowserService` — initializes Chromium, provides navigation, screenshot, form-fill, and element-wait helpers.
- `PlaywrightExtensions` — retry wrapper for Playwright locator operations.

Both `Bootstrap` and `IntegrationTest` projects reference this shared library.

## Screenshots & Logs

- Screenshots are saved to `data/screenshots/integration-test/<test-name>/`.
- Logs are written to `data/logs/integration-test.log`.

These are the primary debugging artifacts when tests fail.

## Test Descriptions

### AuthenticationTest
Verifies the full OAuth login flow: navigates to Mergician, gets redirected to GitLab login, enters test1 credentials, authorizes the app, and confirms the user is authenticated by calling `/api/auth/me`.

### ActivityTest
Pushes a commit to a test repository as test1 (via GitLab API), then logs into Mergician and verifies the push event appears in the activity stream (either in the UI or via the `/api/activity` API).

## Adding New Tests

1. Create a new class in `Tests/` implementing a `Run()` method and `IDisposable`.
2. Instantiate `BrowserService` for browser interaction, call `Initialize()` with a screenshot subdirectory.
3. Register the test in `Program.cs` following the existing pattern.
4. Use `TestConfig` for URLs and credentials rather than hardcoding values.
