namespace Mergician.Entities;

/// <summary>
///     Status values for a branch's merge request. Stored as integers in the database.
///     Lower numbers take priority when calculating group status (Loading beats Blocked beats Ready, etc.).
/// </summary>
public static class MrStatus
{
    /// <summary>Details not yet fetched from GitLab.</summary>
    public const int Loading = 0;

    /// <summary>Blocked from merging: build failed, approvals insufficient, needs rebase, or no MR.</summary>
    public const int Blocked = 1;

    /// <summary>Waiting on a running build or an in-progress rebase.</summary>
    public const int Waiting = 2;

    /// <summary>All checks passed; ready to merge.</summary>
    public const int Ready = 3;
}
