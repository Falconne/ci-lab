---
applyTo: 'src/be/IntegrationTest/**'
---

# Integration Test Guidelines
- Integration tests must exercise the application the same way it is used in production. Do not add special API endpoints, helper methods, or backdoors to the main application solely to support testing.
- Have tests for API endpoints, but also use Playwright to interact with the actual UI to verify what the user sees, especially for dynamic pages.

## Test Design Philosophy: Prioritise Speed Over Granularity

This is a personal productivity tool, not a shared library with external consumers. The integration tests exist to validate that the whole system works end-to-end, especially after AI-assisted changes. With that in mind:

- **Combine related checks into a single test flow** rather than writing isolated test cases per scenario. If verifying feature B requires the same setup as feature A, run both assertions in the same test method rather than repeating the setup.
- **Piggyback on shared state.** Tests run sequentially and share a single browser session. Later tests can rely on state left by earlier ones (e.g. already logged in, existing merge groups on the dashboard). Use `LoginHelper.EnsureLoggedIn` to reuse an existing session rather than logging in fresh each time.
- **Abort on first failure is intentional.** `abortOnFirstFailure = true` in `Program.cs` is by design. When one test fails, subsequent tests are likely to fail too due to shared state, so stopping early gives a clearer signal and avoids misleading noise.
- **Do not write granular independent tests.** There is no requirement for each assertion to be independently reproducible. A test that checks three related things in sequence is better than three tests that each repeat the same setup.
- **Group by feature area.** Each test class covers a feature area (e.g. dashboard, merge group management). Add new scenarios to the most relevant existing class when they share setup with existing checks. Only create a new test class when the feature area is genuinely distinct.
- **Minimise logins.** Each full OAuth login flow takes ~5-10 seconds. Use `LoginHelper.EnsureLoggedIn(browser, username)` to skip the login if the session is already valid for that user. Use `LoginHelper.NavigateToDashboard(browser)` when you just need to return to the dashboard without any user switch.

## Adding Tests for New Functionality

When adding tests for a new feature:

1. Identify which existing test class covers the closest feature area. Add to that class first.
2. Set up test data via `GitLabTestHelper` (branches, MRs) if needed; always clean up in a `finally` block.
3. Use `EnsureLoggedIn` at the start if a session is required; use `NavigateToDashboard` for subsequent sub-tests in the same class.
4. Assert the minimum needed to confirm the feature works — a screenshot + a DOM check is sufficient. Do not add exhaustive edge-case coverage unless a bug was found in that area.
5. Call `_browser.SetScreenshotDir(...)` at the start of `Run()` (already done in each test class) so screenshots are namespaced to the feature area.
