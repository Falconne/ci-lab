namespace IntegrationTest;

public static class TestConfig
{
    // CI Lab test user credentials (matches Bootstrap's ProjectSetupService)
    public const string TestUsername = "test1";
    public const string TestPassword = "changeme123";
    public const string TestEmail = "test1@CILab.local";

    // URLs
    public const string GitLabUrl = "http://localhost:8081";
    public const string MergicianUrl = "http://localhost:5000";

    // Paths
    public static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "data", "screenshots", "integration-test"));

    public static readonly string LogDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "data", "logs"));
}
