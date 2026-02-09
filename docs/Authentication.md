# Authentication

Mergician uses **GitLab OAuth 2.0** for authentication. Users log in with their GitLab credentials — there is no separate user system.

## Flow

1. User visits Mergician. The frontend calls `GET /api/activity` (or any protected endpoint).
2. If no valid session cookie exists, the backend returns `401 Unauthorized`.
3. The frontend detects the 401 and redirects the browser to `GET /api/auth/login`.
4. The `AuthController.Login` action:
   - Generates a random `state` parameter, stores it in an `oauth_state` cookie.
   - Redirects to `{GitLab}/oauth/authorize` with `client_id`, `redirect_uri`, `state`, and scopes `read_user read_api`.
5. User authenticates on GitLab and authorizes the Mergician application.
6. GitLab redirects back to `GET /api/auth/callback?code=...&state=...`.
7. The `AuthController.Callback` action:
   - Validates the `state` parameter against the cookie.
   - Exchanges the authorization `code` for an access/refresh token pair via `POST {GitLab}/oauth/token`.
   - Stores `gl_access_token` and `gl_refresh_token` in `HttpOnly`, `SameSite=Lax` cookies with a 30-day expiry.
   - Redirects to `/` (the frontend).
8. Subsequent API requests read the access token from the cookie. If it expires, the backend transparently refreshes it using the refresh token.

## Key Files

| File | Purpose |
|------|---------|
| `src/be/Mergician/Controllers/AuthController.cs` | Login, callback, `/me`, logout endpoints |
| `src/be/Mergician/Controllers/ActivityController.cs` | Fetches authenticated user's GitLab events |
| `src/be/Mergician/Services/GitLabOAuthService.cs` | Token exchange, user info, events API calls |
| `src/be/Mergician/Services/MergicianSettings.cs` | Strongly-typed settings (GitLab URL, OAuth creds) |
| `src/be/Mergician/appsettings.json` | Default configuration with CI Lab GitLab URL |
| `src/fe/src/views/HomeView.vue` | Activity stream UI; redirects to login on 401 |
| `src/fe/src/components/AppBar.vue` | Displays logged-in user name and logout button |

## Configuration

Settings live under the `Mergician` section in `appsettings.json`:

```json
{
  "Mergician": {
    "GitLab": {
      "Url": "http://localhost:8081",
      "OAuth": {
        "ClientId": "<from GitLab>",
        "ClientSecret": "<from GitLab>"
      }
    }
  }
}
```

These can be overridden by environment variables (e.g. `Mergician__GitLab__OAuth__ClientId`).

## CI Lab Bootstrap

The bootstrapper (`ProjectSetupService.SetupOAuthApplication`) automatically:
- Registers a GitLab OAuth application named "Mergician" with redirect URIs for both `localhost:5000` and `localhost:5173`.
- Saves the resulting `MERGICIAN_GITLAB_OAUTH_CLIENT_ID` and `MERGICIAN_GITLAB_OAUTH_CLIENT_SECRET` to `.env`.

The `mergician-compose.yaml` passes these values through to the backend container.

## Session Persistence

Tokens are stored in cookies with `MaxAge = 30 days`. As long as the refresh token is valid, users stay logged in across browser restarts. The logout endpoint (`POST /api/auth/logout`) deletes both cookies.
