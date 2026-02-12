using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabApprovalState
{
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("approved_by")]
    public List<GitLabApprovalEntry> ApprovedBy { get; set; } = [];
}

public class GitLabApprovalEntry
{
    [JsonPropertyName("user")]
    public GitLabApproverUser? User { get; set; }
}

public class GitLabApproverUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
}
