namespace Mergician.Entities;

/// <summary>
/// Response for the activity poll endpoint, containing new activities
/// and any branch deletion notifications.
/// </summary>
public record ActivityPollResponse(
    List<BranchActivity> Activities,
    List<BranchDeletedNotification> DeletedBranches,
    DateTimeOffset NextPollTime);
