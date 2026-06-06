using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests the Admin page: navigating to it, adding a monitored project by ID, verifying it appears,
///     and removing it. Also verifies the Admin nav tab is visible.
/// </summary>
public class AdminTest
{
    private readonly BrowserService _browser;

    public AdminTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "admin"));

        await TestAdminMonitoredProjects();
        await TestNavigationBackToDashboard();

        Log.Information("Admin tests passed");
    }

    private async Task TestAdminMonitoredProjects()
    {
        Log.Information("Testing: Admin page — monitored projects...");

        await LoginHelper.EnsureLoggedIn(_browser, "test1");
        await _browser.TakeScreenshot("admin_01_dashboard");

        // Navigate to Admin page via nav tab
        var adminTab = _browser.Page.Locator(".v-tab[href='/admin']");
        await adminTab.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await adminTab.ClickAsync();
        await _browser.Page.WaitForURLAsync(
            url => url.Contains("/admin"),
            new PageWaitForURLOptions { Timeout = 15000 });

        await Task.Delay(1500);
        await _browser.TakeScreenshot("admin_02_admin_page");

        // Verify the admin page rendered correctly
        var heading = _browser.Page.GetByText("Admin").First;
        await heading.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

        var addButton = _browser.Page.GetByText("Add Project");
        await addButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        Log.Information("Admin page loaded with 'Add Project' button visible");

        // Resolve project ID for 'primary-1' from GitLab
        var gitLab = new GitLabTestHelper();
        var projectId = gitLab.GetProjectId("primary-1");
        Log.Information("Resolved project ID for 'primary-1': {ProjectId}", projectId);

        // Open Add Project dialog
        await addButton.ClickAsync();
        await Task.Delay(500);
        await _browser.TakeScreenshot("admin_03_add_dialog");

        var projectIdInput = _browser.Page.Locator("input[type='number']");
        await projectIdInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await projectIdInput.FillAsync(projectId.ToString());

        var submitButton = _browser.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add" })
            .Last;

        await submitButton.ClickAsync();
        await _browser.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1500);
        await _browser.TakeScreenshot("admin_04_after_add");

        // Verify project appears in the table — scope to the row to avoid ambiguous text matches
        var projectRow = _browser.Page
            .Locator(".monitored-projects-table tbody tr")
            .Filter(new LocatorFilterOptions { HasText = projectId.ToString() });

        await projectRow.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        Log.Information("Project {ProjectId} appears in monitored projects table", projectId);

        var projectNameCell = projectRow.GetByText("primary-1");
        await projectNameCell.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        Log.Information("Project name 'primary-1' confirmed in table");

        // Delete the project
        var deleteButton = _browser.Page.Locator("button[aria-label='mdi-delete-outline']").First;
        if (await deleteButton.CountAsync() == 0)
        {
            // Fallback: find the delete icon button in the table
            deleteButton = _browser.Page
                .Locator(".monitored-projects-table tbody tr")
                .First
                .Locator(".v-btn");
        }

        await deleteButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await deleteButton.ClickAsync();
        await _browser.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1500);
        await _browser.TakeScreenshot("admin_05_after_delete");

        // Verify the project is no longer in the list
        var remainingRows = await _browser.Page
            .Locator(".monitored-projects-table tbody tr")
            .CountAsync();

        var emptyState = _browser.Page.GetByText("No monitored projects");

        if (remainingRows > 0 && await emptyState.CountAsync() == 0)
        {
            // Check if the specific project ID is still shown in the table
            var stillPresent = await _browser.Page
                .Locator(".monitored-projects-table tbody tr")
                .Filter(new LocatorFilterOptions { HasText = projectId.ToString() })
                .CountAsync();

            if (stillPresent > 0)
            {
                throw new InvalidOperationException(
                    $"Project {projectId} still appears in monitored projects table after deletion");
            }
        }

        Log.Information("Project {ProjectId} successfully removed from monitored projects", projectId);
        Log.Information("Admin monitored projects test passed");
    }

    private async Task TestNavigationBackToDashboard()
    {
        Log.Information("Testing: Admin → Dashboard navigation (blank page regression)...");

        // Click the 'My Merge Groups' (Dashboard) nav tab from the Admin page
        var dashboardTab = _browser.Page.Locator(".v-tab[href='/']");
        await dashboardTab.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await dashboardTab.ClickAsync();

        await _browser.Page.WaitForURLAsync(
            url => !url.Contains("/admin"),
            new PageWaitForURLOptions { Timeout = 15000 });

        await Task.Delay(2000);
        await _browser.TakeScreenshot("admin_06_back_to_dashboard");

        // The dashboard should not be blank — verify it shows merge group data,
        // "No active branches", or "Loading dashboard..." (all are non-blank states).
        var mergeGroupGrid = _browser.Page.Locator(".mg-grid");
        var noActiveBranches = _browser.Page.GetByText("No active branches");
        var loadingDashboard = _browser.Page.GetByText("Loading dashboard");

        // Wait up to 10s for any non-blank content to appear
        var appeared = false;
        for (var i = 0; i < 20 && !appeared; i++)
        {
            if (await mergeGroupGrid.CountAsync() > 0
                || await noActiveBranches.CountAsync() > 0
                || await loadingDashboard.CountAsync() > 0)
            {
                appeared = true;
            }
            else
            {
                await Task.Delay(500);
            }
        }

        if (!appeared)
        {
            throw new InvalidOperationException(
                "Dashboard appears blank after navigating from Admin page. "
                + "Expected merge group grid, 'No active branches', or 'Loading dashboard...' to be visible.");
        }

        // After the initial load, confirm actual merge group data is visible for test1
        await mergeGroupGrid.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        Log.Information("Dashboard loaded correctly after navigating from Admin page");
    }
}