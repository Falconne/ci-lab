namespace Mergician.Entities;

public enum GitLabBranchLookupStatus
{
    Exists,
    Missing,
    Unavailable
}

public record GitLabBranchLookupResult(
    GitLabBranchLookupStatus Status,
    int? StatusCode = null,
    string? Error = null)
{
    public bool Exists => Status == GitLabBranchLookupStatus.Exists;

    public bool IsMissing => Status == GitLabBranchLookupStatus.Missing;

    public bool IsUnavailable => Status == GitLabBranchLookupStatus.Unavailable;
}
