using Microsoft.Playwright;
using Serilog;

namespace IntegrationTest.Services;

/// <summary>
///     Shared helper for waiting until the dashboard UI is fully loaded:
///     grid rows are visible and all per-branch cells have their MR data resolved
///     (each row has either approval info, MR title, or "No MR" text).
/// </summary>
public static class DashboardWaitHelper
{
    /// <summary>
    ///     Waits until dashboard grid rows appear and all per-branch MR cells have resolved.
    ///     Returns true if the dashboard loaded within the timeout, false otherwise.
    /// </summary>
    public static async Task<bool> WaitForDashboardReady(IPage page, int timeoutSeconds = 120)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var rowCount = await page.Locator(".grid-row").CountAsync();
            if (rowCount == 0)
            {
                if (s % 10 == 0)
                {
                    Log.Information("Waiting for dashboard grid rows to appear... {Seconds}s", s);
                }

                await Task.Delay(1000);
                continue;
            }

            // Check all col-mr cells — each must have resolved to either an MR title,
            // "No MR" text, or an approvals indicator (i.e. not still showing a skeleton).
            var mrCells = page.Locator(".grid-row .col-mr");
            var mrCellCount = await mrCells.CountAsync();

            if (mrCellCount == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            var allResolved = true;
            for (var i = 0; i < mrCellCount; i++)
            {
                var cell = mrCells.Nth(i);
                var hasMrTitle = await cell.Locator(".mr-title").CountAsync() > 0;
                var hasNoMr = await cell.Locator(".no-mr-text").CountAsync() > 0;
                var hasSkeleton = await cell.Locator(".skeleton-inline").CountAsync() > 0;

                if (!hasMrTitle && !hasNoMr && hasSkeleton)
                {
                    allResolved = false;
                    break;
                }
            }

            if (allResolved)
            {
                Log.Information(
                    "Dashboard fully loaded after ~{Seconds}s ({Rows} rows, {Cells} MR cells resolved)",
                    s,
                    rowCount,
                    mrCellCount);

                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for MR data to resolve... {Rows} rows visible, {Seconds}s elapsed",
                    rowCount,
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    ///     Waits until each specified branch shows the expected group status badge in the grid.
    ///     Useful for waiting until background build jobs complete and Mergician reflects the final state.
    /// </summary>
    /// <param name="page">The Playwright page to poll.</param>
    /// <param name="expectedStatuses">Map of branch name (substring) → expected status label (e.g. "Ready").</param>
    /// <param name="timeoutSeconds">Maximum seconds to wait.</param>
    /// <returns>True if all statuses matched within the timeout, false otherwise.</returns>
    public static async Task<bool> WaitForGroupStatuses(
        IPage page,
        IReadOnlyDictionary<string, string> expectedStatuses,
        int timeoutSeconds = 180)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var allMatch = true;

            foreach (var (branchName, expectedStatus) in expectedStatuses)
            {
                var rows = page.Locator($"[data-mg-name*='{branchName}']");
                var rowCount = await rows.CountAsync();
                var matched = false;

                if (rowCount > 0)
                {
                    var badge = rows.First.Locator(".card-status-badge");
                    var status = await badge.CountAsync() > 0
                        ? (await badge.InnerTextAsync()).Trim()
                        : "";

                    if (status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
                        matched = true;
                }

                if (!matched)
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                Log.Information(
                    "All expected group statuses matched after ~{Seconds}s",
                    s);
                return true;
            }

            if (s % 15 == 0 && s > 0)
            {
                Log.Information(
                    "Waiting for expected group statuses... {Seconds}s elapsed",
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }
}