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
}