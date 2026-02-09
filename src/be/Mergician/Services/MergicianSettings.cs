namespace Mergician.Services;

public class MergicianSettings
{
    public GitLabSettings GitLab { get; set; } = new();
}

public class GitLabSettings
{
    public string Url { get; set; } = "http://localhost:8081";
    public OAuthSettings OAuth { get; set; } = new();
}

public class OAuthSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
