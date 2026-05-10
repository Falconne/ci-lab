using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Extended merge request details including merge status and conflict information.
///     Used by the AutoMergeService to determine if an MR is ready to be merged.
/// </summary>
public class GitLabDetailedMergeRequest
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";

    [JsonPropertyName("target_branch")]
    public string TargetBranch { get; set; } = "";

    [JsonPropertyName("merge_status")]
    public string MergeStatus { get; set; } = "";

    [JsonPropertyName("detailed_merge_status")]
    public string DetailedMergeStatus { get; set; } = "";

    [JsonPropertyName("has_conflicts")]
    public bool HasConflicts { get; set; }

    [JsonPropertyName("diverged_commits_count")]
    public int? DivergedCommitsCount { get; set; }

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    [JsonPropertyName("rebase_in_progress")]
    public bool RebaseInProgress { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}