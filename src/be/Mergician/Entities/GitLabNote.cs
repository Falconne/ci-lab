using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Response from the GitLab merge request notes (comments) API.
/// </summary>
public class GitLabNote
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}