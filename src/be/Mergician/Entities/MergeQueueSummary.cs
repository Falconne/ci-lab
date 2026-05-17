namespace Mergician.Entities;

/// <summary>
///     Summary of a merge queue, returned by the queue list API.
/// </summary>
public record MergeQueueSummary(
    int QueueId,

    /// <summary>Display name derived from the project names in the queue.</summary>
    string DisplayName,

    /// <summary>Number of merge groups currently in the queue.</summary>
    int EntryCount,

    /// <summary>True if the requesting user has at least one tracked merge group in this queue.</summary>
    bool HasTrackedGroups);
