---
applyTo: 'src/be/IntegrationTest/**'
---

## Integration Test Guidelines
- Integration tests must exercise the application the same way it is used in production. Do not add special API endpoints, helper methods, or backdoors to the main application solely to support testing.
- Use Playwright to interact with the actual UI rather than calling internal API endpoints directly, unless the API endpoint itself is the feature being tested (e.g. `/api/auth/me` for verifying auth status).
- If a feature is consumed via SSE streaming in the frontend, the integration test should verify it through the rendered UI after the stream completes — not by calling a separate non-streaming endpoint.
- Tests should verify observable behaviour (what the user sees), not internal implementation details.
