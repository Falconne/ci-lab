namespace Mergician.Entities;

/// <summary>
///     Summary of a merge queue, used to populate the queue selector combobox.
/// </summary>
public record MergeQueueSummary(
    int QueueId,

    /// <summary>Display name derived from the project names in the queue.</summary>
    string DisplayName,

    /// <summary>Number of merge groups currently in the queue.</summary>
    int EntryCount);
