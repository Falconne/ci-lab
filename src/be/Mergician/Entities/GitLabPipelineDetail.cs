using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Pipeline details including status. Used by AutoMergeService
///     to check if the latest pipeline has passed.
/// </summary>
public class GitLabPipelineDetail
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = "";
}