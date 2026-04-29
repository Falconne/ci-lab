using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Response from <c>GET /projects/:id/merge_requests/:iid/approvals</c>.
///     Used as a fallback when the <c>/approval_state</c> endpoint is unavailable (e.g. 404).
/// </summary>
public class GitLabApprovalState
{
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("approvals_required")]
    public int? ApprovalsRequired { get; set; }

    [JsonPropertyName("approved_by")]
    public List<GitLabApprovalEntry> ApprovedBy { get; set; } = [];
}

/// <summary>
///     Response from <c>GET /projects/:id/merge_requests/:iid/approval_state</c>.
///     Contains per-rule approval data so we can aggregate only approvals that satisfy rules.
/// </summary>
public class GitLabApprovalStateDetails
{
    [JsonPropertyName("rules")]
    public List<GitLabApprovalRule> Rules { get; set; } = [];
}

/// <summary>
///     One approval rule within a <see cref="GitLabApprovalStateDetails" /> response.
/// </summary>
public class GitLabApprovalRule
{
    [JsonPropertyName("approvals_required")]
    public int ApprovalsRequired { get; set; }

    /// <summary>Users whose approvals count towards satisfying this rule.</summary>
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