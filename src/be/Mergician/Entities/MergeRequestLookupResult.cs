namespace Mergician.Entities;

public record MergeRequestLookupResult(GitLabProject Project, string SourceBranch, string MergeRequestTitle);
