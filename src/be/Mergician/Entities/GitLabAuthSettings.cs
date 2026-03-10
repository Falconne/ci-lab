namespace Mergician.Entities;

/// <summary>
///     Configuration for the GitLab authentication handler,
///     containing the resolved GitLab API base URL.
/// </summary>
public class GitLabAuthSettings
{
    public string ApiBaseUrl { get; init; } = "";
}