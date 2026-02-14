---
applyTo: 'src/be/IntegrationTest/**'
---

## Integration Test Guidelines
- Integration tests must exercise the application the same way it is used in production. Do not add special API endpoints, helper methods, or backdoors to the main application solely to support testing.
- Have tests for API endpoints, but also use Playwright to interact with the actual UI to verify what the user sees, especially for dynamic pages.
