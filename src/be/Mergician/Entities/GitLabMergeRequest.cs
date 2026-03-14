using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabMergeRequest
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";
}