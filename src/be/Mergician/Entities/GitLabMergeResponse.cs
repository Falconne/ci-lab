using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Response from the GitLab merge (accept) merge request API.
/// </summary>
public class GitLabMergeResponse
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";
}