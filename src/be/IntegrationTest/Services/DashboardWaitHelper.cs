using Microsoft.Playwright;
using Serilog;

namespace IntegrationTest.Services;

/// <summary>
///     Shared helper for waiting until the dashboard UI is fully loaded:
///     cards are visible and all card items have their MR data resolved
///     (each item has either approval info, MR title, or "No Merge Request" text).
/// </summary>
public static class DashboardWaitHelper
{
    /// <summary>
    ///     Waits until dashboard cards appear and all card items have resolved MR data.
    ///     Returns true if the dashboard loaded within the timeout, false otherwise.
    /// </summary>
    public static async Task<bool> WaitForDashboardReady(IPage page, int timeoutSeconds = 120)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var cardCount = await page.Locator(".merge-group-card").CountAsync();
            if (cardCount == 0)
            {
                if (s % 10 == 0)
                {
                    Log.Information("Waiting for dashboard cards to appear... {Seconds}s", s);
                }

                await Task.Delay(1000);
                continue;
            }

            var items = page.Locator(".card-item");
            var itemCount = await items.CountAsync();

            if (itemCount == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            var allResolved = true;
            for (var i = 0; i < itemCount; i++)
            {
                var item = items.Nth(i);
                var hasApprovals = await item.Locator(".item-approvals").CountAsync() > 0;
                var hasNoMergeRequest = await item.Locator(".item-no-mr").CountAsync() > 0;
                var hasMergeRequestTitle = await item.Locator(".item-mr-title").CountAsync() > 0;

                if (!hasApprovals && !hasNoMergeRequest && !hasMergeRequestTitle)
                {
                    allResolved = false;
                    break;
                }
            }

            if (allResolved)
            {
                Log.Information(
                    "Dashboard fully loaded after ~{Seconds}s ({Cards} cards, {Items} items resolved)",
                    s,
                    cardCount,
                    itemCount);

                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for MR data to resolve... {Cards} cards visible, {Seconds}s elapsed",
                    cardCount,
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    ///     Waits until each specified branch shows the expected group status badge on the dashboard.
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
                var cardElements = page.Locator(".merge-group-card");
                var cardCount = await cardElements.CountAsync();
                var matched = false;

                for (var i = 0; i < cardCount; i++)
                {
                    var card = cardElements.Nth(i);
                    var name = (await card.Locator(".branch-name").InnerTextAsync()).Trim();
                    if (!name.Contains(branchName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var badge = card.Locator(".card-status-badge");
                    var status = await badge.CountAsync() > 0
                        ? (await badge.InnerTextAsync()).Trim()
                        : "";

                    if (status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
                        matched = true;

                    break;
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