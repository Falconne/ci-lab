using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabBranchDetails
{
    [JsonPropertyName("commit")]
    public GitLabBranchCommit? Commit { get; set; }
}

public class GitLabBranchCommit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("committed_date")]
    public DateTimeOffset? CommittedDate { get; set; }
}