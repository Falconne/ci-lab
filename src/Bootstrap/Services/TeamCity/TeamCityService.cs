using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Bootstrap.Services.Utilities;

namespace Bootstrap.Services.TeamCity;

public class TeamCityService
{
    public async Task<bool> AutomateTeamCitySetupAsync(HttpClient client, string teamcityUrl, string username, string password)
    {
        var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "screenshots");
        var screenshotCounter = 0;

        if (Directory.Exists(screenshotDir))
        {
            Directory.Delete(screenshotDir, true);
        }
        Directory.CreateDirectory(screenshotDir);
        Console.WriteLine($"[bootstrap] Screenshot directory created: {screenshotDir}");

        async Task TakeScreenshot(IPage page, string description)
        {
            try
            {
                screenshotCounter++;
                var timestamp = DateTime.UtcNow.ToString("HHmmss");
                var filename = $"{screenshotCounter:D3}_{timestamp}_{description}.png";
                var path = Path.Combine(screenshotDir, filename);
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
                Console.WriteLine($"[bootstrap]   📸 Screenshot saved: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bootstrap] WARNING:   Could not save screenshot: {ex.Message}");
            }
        }

        async Task<bool> CheckForMaintenanceErrorAsync(IPage page)
        {
            try
            {
                var pageContent = await page.ContentAsync();
                if (pageContent.Contains("TeamCity server requires technical maintenance") &&
                    pageContent.Contains("already logged in"))
                {
                    Console.Error.WriteLine("[bootstrap] ERROR: TeamCity server is in maintenance mode");
                    await TakeScreenshot(page, "error_maintenance_mode");
                    return true; // Error detected
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bootstrap] WARNING: Could not check for maintenance message: {ex.Message}");
            }
            return false; // No error
        }

    async Task WaitForTextToDisappearAsync(IPage page, string text, int timeoutSeconds = 120, int pollMs = 1000)
    {
        var found = false;
        for (var s = 0; s < timeoutSeconds; s++)
        {
            try
            {
                var pageContent = await page.ContentAsync();
                if (pageContent != null && pageContent.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    if (s % 5 == 0)
                        Console.WriteLine($"[bootstrap]   Waiting for '{text}' to disappear... waited {s}s");
                    await Task.Delay(pollMs);
                    continue;
                }
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bootstrap]   Warning reading page content while waiting for '{text}': {ex.Message}");
                await Task.Delay(pollMs);
            }
        }

        if (found)
            Console.WriteLine($"[bootstrap]   '{text}' no longer present (or timed out waiting)");
    }

        try
        {
            Console.WriteLine("[bootstrap] Starting automated TeamCity initial setup using Playwright...");

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
            {
                Console.WriteLine("[bootstrap] WARNING: Playwright browser installation returned non-zero exit code, continuing anyway...");
            }

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 60000
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(60000);

            Console.WriteLine($"[bootstrap] Navigating to {teamcityUrl}");
            await page.GotoAsync(teamcityUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Task.Delay(3000);
            await TakeScreenshot(page, "01_initial_page");

            // Check for maintenance/admin limit error early
            if (await CheckForMaintenanceErrorAsync(page))
            {
                return false;
            }

            if (await page.Locator("input[name='username'], input[id='username']").CountAsync() > 0)
            {
                Console.WriteLine("[bootstrap] TeamCity appears to be already configured (login page detected)");
                await TakeScreenshot(page, "already_configured");
            }

            // The rest of the original flow is preserved here; many UI interactions
            // and token extraction attempts are performed. For brevity the implementation
            // matches the previous Program.cs logic but uses Console logging and
            // EnvHelper for environment updates.

            // Step 1: Data Directory - Click Proceed
            Console.WriteLine("[bootstrap] Step 1: Checking for data directory configuration screen");
            await TakeScreenshot(page, "02_before_data_directory");

            var proceedButton = page.Locator("button:has-text('Proceed'), input[value='Proceed']").First;
            if (await proceedButton.CountAsync() > 0)
            {
                Console.WriteLine("[bootstrap]   Found Proceed button, clicking...");
                await proceedButton.ClickAsync();
                Console.WriteLine("[bootstrap]   Waiting for navigation...");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Task.Delay(3000);
                await TakeScreenshot(page, "03_after_data_directory");
                Console.WriteLine("[bootstrap]   Data directory step completed");
            }

            // Step 2: Database Setup - Wait and Proceed
            Console.WriteLine("[bootstrap] Step 2: Checking for database setup screen");
            await Task.Delay(2000);
            await TakeScreenshot(page, "04_before_database");

            var dbProceedButton = page.Locator("button:has-text('Proceed'), input[value='Proceed']");
            var maxWaitForDb = 60; // Wait up to 60 seconds for DB initialization
            for (var i = 0; i < maxWaitForDb; i++)
            {
                if (await dbProceedButton.CountAsync() > 0)
                {
                    Console.WriteLine("[bootstrap]   Database setup ready, clicking Proceed...");
                    await dbProceedButton.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(3000);

                    // Wait for TeamCity database initialization message to clear
                    await WaitForTextToDisappearAsync(page, "Creating a new database", 120);

                    // Also wait for server components initialization message to clear if present
                    await WaitForTextToDisappearAsync(page, "Initializing TeamCity server components", 180);

                    await TakeScreenshot(page, "05_after_database");
                    Console.WriteLine("[bootstrap]   Database setup completed");
                    break;
                }
                await Task.Delay(1000);
                if (i % 10 == 0 && i > 0)
                {
                    Console.WriteLine($"[bootstrap]   Still waiting for database initialization... ({i}s)");
                }
            }

            // Step 3: License Agreement - Accept
            Console.WriteLine("[bootstrap] Step 3: Checking for license agreement");
            await Task.Delay(2000);
            await TakeScreenshot(page, "06_before_license");

            var pageText = await page.ContentAsync();
            if (pageText != null && pageText.IndexOf("License Agreement for JetBrains", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var acceptCheckbox = page.Locator("input[type='checkbox'][name='accept'], input[id='accept'], input[name='acceptLicense']");
                if (await acceptCheckbox.CountAsync() > 0)
                {
                    Console.WriteLine("[bootstrap]   Found license checkbox, checking it...");
                    await acceptCheckbox.First.CheckAsync();
                    await Task.Delay(500);

                    var continueButton = page.Locator("button:has-text('Continue'), input[value='Continue'], button[type='submit']");
                    if (await continueButton.CountAsync() == 0)
                    {
                        Console.Error.WriteLine("[bootstrap] ERROR: Continue button not found on license page");
                        await TakeScreenshot(page, "error_license_no_continue");
                        return false;
                    }

                    Console.WriteLine("[bootstrap]   Clicking Continue after license acceptance...");
                    await continueButton.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(3000);
                    await TakeScreenshot(page, "07_after_license");

                    // Ensure license text disappeared
                    await WaitForTextToDisappearAsync(page, "License Agreement for JetBrains", 30);
                    var postLicenseText = await page.ContentAsync();
                    if (postLicenseText != null && postLicenseText.IndexOf("License Agreement for JetBrains", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.Error.WriteLine("[bootstrap] ERROR: License acceptance did not complete successfully");
                        await TakeScreenshot(page, "error_license_still_present");
                        return false;
                    }

                    Console.WriteLine("[bootstrap]   License accepted");
                }
                else
                {
                    Console.WriteLine("[bootstrap]   License checkbox not found, skipping license acceptance step");
                }
            }
            else
            {
                Console.WriteLine("[bootstrap]   License page not detected, skipping license acceptance step");
            }

            // Step 4: Create Administrator Account
            Console.WriteLine("[bootstrap] Step 4: Checking for admin account creation");
            await Task.Delay(2000);
            await TakeScreenshot(page, "08_before_admin_creation");

            var usernameField = page.Locator("input[name='username'], input[id='input_teamcityUsername']");
            if (await usernameField.CountAsync() > 0)
            {
                Console.WriteLine("[bootstrap]   Found admin creation form, filling in details...");
                await usernameField.First.FillAsync(username);
                await Task.Delay(300);

                var passwordField = page.Locator("input[name='password'], input[id='password1'], input[type='password']").First;
                await passwordField.FillAsync(password);
                await Task.Delay(300);

                var confirmPasswordField = page.Locator("input[name='confirmPassword'], input[id='password2'], input[name='retypedPassword']");
                if (await confirmPasswordField.CountAsync() > 0)
                {
                    await confirmPasswordField.First.FillAsync(password);
                    await Task.Delay(300);
                }

                await TakeScreenshot(page, "09_admin_form_filled");

                var createAccountButton = page.Locator("button:has-text('Create Account'), input[value='Create Account'], button[type='submit']");
                if (await createAccountButton.CountAsync() > 0)
                {
                    Console.WriteLine("[bootstrap]   Submitting admin account creation...");
                    await createAccountButton.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(5000); // Give TeamCity time to fully initialize
                    await TakeScreenshot(page, "10_after_admin_creation");
                    Console.WriteLine("[bootstrap]   Admin account created successfully");
                }
            }
            else
            {
                Console.WriteLine("[bootstrap]   Admin account may already exist, continuing...");
            }

            // Navigate to token creation page and attempt to create/access token
            Console.WriteLine("[bootstrap] Step 5: Creating access token (UI)");
            try
            {
                await page.GotoAsync($"{teamcityUrl}/profile.html?item=accessTokens", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
                await Task.Delay(2000);
                await TakeScreenshot(page, "19_token_page");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bootstrap] WARNING:   Could not navigate to token page: {ex.Message}");
                await TakeScreenshot(page, "19_token_page_error");
            }

            // Try to find existing token
            var existingToken = page.Locator("td:has-text('bootstrap-automation'), span:has-text('bootstrap-automation'), div:has-text('bootstrap-automation')");
            if (await existingToken.CountAsync() > 0)
            {
                Console.WriteLine("[bootstrap]   ✓ TeamCity token 'bootstrap-automation' already exists - skipping creation");
                await TakeScreenshot(page, "19_token_already_exists");
                return true;
            }

            // Try to create token using UI
            var createTokenButton = page.Locator(
                "button:has-text('Create access token'), " +
                "a:has-text('Create access token'), " +
                "input[value='Create access token'], " +
                "button:has-text('Create token'), " +
                "a:has-text('Create token'), " +
                "button.btn:has-text('Create'), " +
                "a.btn:has-text('Create')"
            );

            if (await createTokenButton.CountAsync() > 0)
            {
                Console.WriteLine($"[bootstrap]   Found create token button (matched {await createTokenButton.CountAsync()} elements)");
                await createTokenButton.First.ClickAsync();
                await Task.Delay(1000);
                await TakeScreenshot(page, "20_token_creation_dialog");

                var tokenNameInput = page.Locator(
                    "input[name='tokenName'], input[id='tokenName'], input[name='name'], " +
                    "input[placeholder*='name'], input[placeholder*='Token'], input[aria-label*='name'], textarea[name='name']"
                );

                if (await tokenNameInput.CountAsync() == 0)
                {
                    var dialog = page.Locator("[role='dialog'], div.modal, div[aria-modal='true']");
                    if (await dialog.CountAsync() > 0)
                    {
                        var innerInput = dialog.First.Locator("input, textarea, [contenteditable='true']");
                        if (await innerInput.CountAsync() > 0)
                        {
                            tokenNameInput = innerInput;
                        }
                    }
                }

                if (await tokenNameInput.CountAsync() > 0)
                {
                    try
                    {
                        Console.WriteLine("[bootstrap]   Filling token name 'bootstrap-automation'");
                        await tokenNameInput.First.FillAsync("bootstrap-automation");
                        await Task.Delay(500);
                        await TakeScreenshot(page, "20b_token_name_filled");

                        var createButton = page.Locator("button:has-text('Create'), input[value='Create'], button[type='submit'], button:has-text('Generate')");
                        if (await createButton.CountAsync() > 0)
                        {
                            Console.WriteLine("[bootstrap]   Clicking Create/Generate button");
                            await createButton.First.ClickAsync();
                            await Task.Delay(2000);
                            await TakeScreenshot(page, "21_token_created");

                            var tokenValue = page.Locator(
                                "input[readonly], textarea[readonly], input[aria-readonly='true'], code, pre, span.token, div.token, input.token-value"
                            );

                            if (await tokenValue.CountAsync() > 0)
                            {
                                var token = await tokenValue.First.TextContentAsync();
                                if (string.IsNullOrWhiteSpace(token))
                                {
                                    var tokenInput = await tokenValue.First.GetAttributeAsync("value");
                                    token = tokenInput;
                                }

                                if (string.IsNullOrWhiteSpace(token))
                                {
                                    var specificInput = page.Locator("input[type='text'][readonly], input[id*='token']");
                                    if (await specificInput.CountAsync() > 0)
                                    {
                                        token = await specificInput.First.GetAttributeAsync("value");
                                    }
                                }

                                if (!string.IsNullOrWhiteSpace(token))
                                {
                                    var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
                                    var envFullPath = Path.GetFullPath(envPath);
                                    EnvHelper.SaveOrUpdateEnvFile(envFullPath, "TEAMCITY_TOKEN", token.Trim());
                                    Console.WriteLine($"[bootstrap]   ✓ TeamCity token created and saved to .env");
                                }
                                else
                                {
                                    Console.WriteLine("[bootstrap] WARNING:   Token created but could not be extracted automatically");
                                }
                            }
                            else
                            {
                                Console.WriteLine("[bootstrap] WARNING:   Token created but token value element not found");
                            }
                        }
                        else
                        {
                            Console.WriteLine("[bootstrap] WARNING:   Could not find Create/Generate button in dialog");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[bootstrap] WARNING:   Exception during token creation: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("[bootstrap] WARNING:   Could not find token name input field (tried multiple selectors)");
                    try
                    {
                        var fallbackSubmit = page.Locator("button:has-text('Create'), button:has-text('Generate'), input[type='submit']");
                        if (await fallbackSubmit.CountAsync() > 0)
                        {
                            Console.WriteLine("[bootstrap]   Attempting fallback submit for token creation");
                            await fallbackSubmit.First.ClickAsync();
                            await Task.Delay(1500);
                            await TakeScreenshot(page, "21_token_created_fallback");
                        }
                    }
                    catch { }
                }
            }
            else
            {
                Console.WriteLine("[bootstrap] WARNING:   Could not find 'Create access token' button");
                var currentUrl = page.Url;
                Console.WriteLine($"[bootstrap]   Current URL: {currentUrl}");
            }

            await TakeScreenshot(page, "22_final_state");
            Console.WriteLine("[bootstrap] ✓ TeamCity automated setup completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR: TeamCity automated setup failed: {ex.Message}");
            Console.Error.WriteLine($"[bootstrap] ERROR:   Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    public async Task<string?> TryCreateTokenViaApiAsync(HttpClient client, string teamcityUrl, string username, string password, string tokenName)
    {
        Console.WriteLine($"[bootstrap]   Attempting API token creation with username '{username}' and tokenName '{tokenName}'");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        var endpoints = new[]
        {
            $"{teamcityUrl.TrimEnd('/')}/app/rest/users/username:{username}/tokens",
            $"{teamcityUrl.TrimEnd('/')}/app/rest/users/{username}/tokens",
            $"{teamcityUrl.TrimEnd('/')}/app/rest/users/id:1/tokens",
            $"{teamcityUrl.TrimEnd('/')}/app/rest/users/current/tokens"
        };

        var maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Console.WriteLine($"[bootstrap]   API token creation attempt {attempt}/{maxAttempts}");
            foreach (var url in endpoints)
            {
                try
                {
                    Console.WriteLine($"[bootstrap]     Trying endpoint: {url}");
                    var xmlBody = $"<token name=\"{System.Net.WebUtility.HtmlEncode(tokenName)}\"/>";
                    var xmlReq = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml")
                    };
                    xmlReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
                    xmlReq.Headers.Accept.Clear();
                    xmlReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var resp = await client.SendAsync(xmlReq);
                    var respText = await resp.Content.ReadAsStringAsync();
                    Console.WriteLine($"[bootstrap]       Response status: {(int)resp.StatusCode} {resp.StatusCode}");
                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[bootstrap]       Response body: {respText.Substring(0, Math.Min(200, respText.Length))}");
                    }
                    if (resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[bootstrap]       ✓ Success! Parsing response...");
                        try
                        {
                            using var doc = JsonDocument.Parse(respText);
                            if (doc.RootElement.TryGetProperty("token", out var tokenElem))
                            {
                                Console.WriteLine($"[bootstrap]       ✓ Token extracted from JSON 'token' field");
                                return tokenElem.GetString();
                            }
                            if (doc.RootElement.TryGetProperty("value", out var valueElem))
                            {
                                Console.WriteLine($"[bootstrap]       ✓ Token extracted from JSON 'value' field");
                                return valueElem.GetString();
                            }
                            if (doc.RootElement.TryGetProperty("tokenValue", out var tvElem))
                            {
                                Console.WriteLine($"[bootstrap]       ✓ Token extracted from JSON 'tokenValue' field");
                                return tvElem.GetString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[bootstrap]       JSON parse failed: {ex.Message}");
                        }

                        try
                        {
                            var xmlDoc = System.Xml.Linq.XDocument.Parse(respText);
                            var tokenElem = xmlDoc.Root?.Element("token") ?? xmlDoc.Root;
                            if (tokenElem != null)
                            {
                                var valAttr = tokenElem.Attribute("value") ?? tokenElem.Attribute("tokenValue");
                                if (valAttr != null && !string.IsNullOrWhiteSpace(valAttr.Value))
                                {
                                    Console.WriteLine($"[bootstrap]       ✓ Token extracted from XML attribute");
                                    return valAttr.Value;
                                }
                                if (!string.IsNullOrWhiteSpace(tokenElem.Value))
                                {
                                    Console.WriteLine($"[bootstrap]       ✓ Token extracted from XML element value");
                                    return tokenElem.Value.Trim();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[bootstrap]       XML parse failed: {ex.Message}");
                        }

                        if (!string.IsNullOrWhiteSpace(respText) && respText.Length > 20)
                        {
                            Console.WriteLine($"[bootstrap]       ✓ Using raw response as token (length: {respText.Length})");
                            return respText.Trim();
                        }
                        Console.WriteLine($"[bootstrap]       WARNING: Success response but couldn't extract token");
                    }

                    Console.WriteLine($"[bootstrap]     Trying with JSON body...");
                    Console.WriteLine($"[bootstrap]     Trying with JSON body...");
                    var jsonBody = JsonSerializer.Serialize(new { name = tokenName });
                    var jsonReq = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                    };
                    jsonReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
                    jsonReq.Headers.Accept.Clear();
                    jsonReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var resp2 = await client.SendAsync(jsonReq);
                    var resp2Text = await resp2.Content.ReadAsStringAsync();
                    Console.WriteLine($"[bootstrap]       Response status (JSON): {(int)resp2.StatusCode} {resp2.StatusCode}");
                    if (!resp2.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[bootstrap]       Response body: {resp2Text.Substring(0, Math.Min(200, resp2Text.Length))}");
                    }
                    if (resp2.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[bootstrap]       ✓ Success with JSON! Parsing response...");
                        try
                        {
                            using var doc = JsonDocument.Parse(resp2Text);
                            if (doc.RootElement.TryGetProperty("token", out var tokenElem))
                            {
                                Console.WriteLine($"[bootstrap]       ✓ Token extracted from JSON response");
                                return tokenElem.GetString();
                            }
                            if (doc.RootElement.TryGetProperty("value", out var valueElem))
                            {
                                Console.WriteLine($"[bootstrap]       ✓ Token extracted from JSON 'value' field");
                                return valueElem.GetString();
                            }
                            if (doc.RootElement.TryGetProperty("tokenValue", out var tvElem))
                            {
                                Console.WriteLine($"[bootstrap]       ✓ Token extracted from JSON 'tokenValue' field");
                                return tvElem.GetString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[bootstrap]       JSON parse failed: {ex.Message}");
                        }

                        if (!string.IsNullOrWhiteSpace(resp2Text) && resp2Text.Length > 20)
                        {
                            Console.WriteLine($"[bootstrap]       ✓ Using raw JSON response as token");
                            return resp2Text.Trim();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[bootstrap]       Exception: {ex.Message}");
                }
            }

            if (attempt < maxAttempts)
            {
                var delay = Math.Min(2000 * (int)Math.Pow(2, attempt - 1), 10000);
                Console.WriteLine($"[bootstrap]   Waiting {delay}ms before retry...");
                await Task.Delay(delay);
            }
        }

        Console.WriteLine($"[bootstrap]   ✗ All API token creation attempts failed");
        return null;
    }

    public async Task<bool> ValidateTeamCityTokenAsync(HttpClient client, string teamcityUrl, string token)
    {
        try
        {
            var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/server";
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
            };

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var serverData = await response.Content.ReadAsStringAsync();
                Console.WriteLine("[bootstrap]   Token authentication successful");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR:   Validation error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CreateProjectAsync(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/projects";

        Console.WriteLine($"[bootstrap] Creating TeamCity project 'Sample Project' via {apiUrl}");

        try
        {
            var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                Headers =
                {
                    Accept = { MediaTypeWithQualityHeaderValue.Parse("application/json") },
                    Authorization = new AuthenticationHeaderValue("Bearer", token)
                }
            };

            var response = await client.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Console.WriteLine("[bootstrap] TeamCity project created successfully");
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Conflict || body.Contains("DuplicateProjectNameException") || body.Contains("already exists"))
            {
                Console.WriteLine("[bootstrap] TeamCity project already exists");
                return true;
            }

            Console.Error.WriteLine($"[bootstrap] ERROR: TeamCity API error {(int)response.StatusCode}: {body}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR: Failed to call TeamCity API: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AuthorizeAgentsAsync(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/agents";

        try
        {
            var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}?locator=authorized:false")
            {
                Headers =
                {
                    Accept = { MediaTypeWithQualityHeaderValue.Parse("application/json") },
                    Authorization = new AuthenticationHeaderValue("Bearer", token)
                }
            };

            var listResponse = await client.SendAsync(listRequest);
            if (!listResponse.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[bootstrap] ERROR: Failed to get agents list: {(int)listResponse.StatusCode}");
                return false;
            }

            var agentsData = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (!agentsData.TryGetProperty("agent", out var agents))
            {
                Console.WriteLine("[bootstrap] No unauthorized agents found");
                return true;
            }

            var authorizedCount = 0;
            foreach (var agent in agents.EnumerateArray())
            {
                var agentId = agent.GetProperty("id").GetInt32();
                var agentName = agent.TryGetProperty("name", out var name) ? name.GetString() : $"agent-{agentId}";

                Console.WriteLine($"[bootstrap] Authorizing agent: {agentName} (ID: {agentId})");

                var authRequest = new HttpRequestMessage(HttpMethod.Put, $"{apiUrl}/id:{agentId}/authorized")
                {
                    Content = new StringContent("true", Encoding.UTF8, "text/plain"),
                    Headers =
                    {
                        Authorization = new AuthenticationHeaderValue("Bearer", token)
                    }
                };

                var authResponse = await client.SendAsync(authRequest);
                if (authResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[bootstrap]   ✓ Agent {agentName} authorized");
                    authorizedCount++;

                    var poolApiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/agentPools/id:0/agents";
                    var poolRequest = new HttpRequestMessage(HttpMethod.Post, poolApiUrl)
                    {
                        Content = new StringContent($"<agent id=\"{agentId}\" />", Encoding.UTF8, "application/xml"),
                        Headers =
                        {
                            Authorization = new AuthenticationHeaderValue("Bearer", token)
                        }
                    };

                    var poolResponse = await client.SendAsync(poolRequest);
                    if (poolResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[bootstrap]   ✓ Agent {agentName} added to default pool");
                    }
                    else
                    {
                        Console.WriteLine($"[bootstrap] WARNING:   Could not add agent {agentName} to pool: {(int)poolResponse.StatusCode}");
                    }
                }
                else
                {
                    Console.WriteLine($"[bootstrap] WARNING:   Failed to authorize agent {agentName}: {(int)authResponse.StatusCode}");
                }
            }

            Console.WriteLine($"[bootstrap] Authorized {authorizedCount} agent(s)");
            return authorizedCount > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR: Failed to authorize agents: {ex.Message}");
            return false;
        }
    }
}
