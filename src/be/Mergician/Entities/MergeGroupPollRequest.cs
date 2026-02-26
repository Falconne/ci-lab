namespace Mergician.Entities;

/// <summary>
///     Request payload for the merge group poll endpoint.
///     The frontend sends the branch database IDs it currently displays so the backend
///     can compute a minimal diff against the current database state for this merge group.
/// </summary>
public record MergeGroupPollRequest(List<KnownBranch> KnownBranches);
