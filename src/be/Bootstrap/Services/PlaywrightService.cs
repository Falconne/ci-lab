// This file re-exports the shared PlaywrightService.BrowserService for backward compatibility.
// All Playwright functionality lives in the shared PlaywrightService project.

using PlaywrightService;

namespace Bootstrap.Services;

/// <summary>
///     Thin wrapper around the shared BrowserService for backward compatibility
///     within the Bootstrap project.
/// </summary>
public class PlaywrightService : BrowserService
{
}