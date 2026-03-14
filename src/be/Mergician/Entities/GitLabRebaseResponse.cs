using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Response from the GitLab rebase merge request API.
/// </summary>
public class GitLabRebaseResponse
{
    [JsonPropertyName("rebase_in_progress")]
    public bool RebaseInProgress { get; set; }
}