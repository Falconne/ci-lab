namespace Mergician.Entities;

/// <summary>
///     Summary of a merge queue, returned by the queue list API.
/// </summary>
public record MergeQueueSummary(
    int QueueId,
    string DisplayName,
    int EntryCount,
    bool HasTrackedGroups);