using System.Text.Json.Serialization;

namespace IntegrationTest.Entities;

public record GitLabProjectInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public record GitLabMrInfo(
    [property: JsonPropertyName("iid")] int Iid);

public record GitLabMrDetail(
    [property: JsonPropertyName("iid")] int Iid,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("source_branch")]
    string SourceBranch,
    [property: JsonPropertyName("detailed_merge_status")]
    string DetailedMergeStatus,
    [property: JsonPropertyName("has_conflicts")]
    bool HasConflicts,
    [property: JsonPropertyName("diverged_commits_count")]
    int DivergedCommitsCount);

public record GitLabBranchCommit(
    [property: JsonPropertyName("id")] string Id);

public record GitLabBranchInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("commit")] GitLabBranchCommit Commit);