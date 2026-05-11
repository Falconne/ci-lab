namespace Mergician.Entities.Database;

public class MergeGroupBase
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    // Populated via Dapper and serialized to the Vue frontend.
    // ReSharper disable UnusedMember.Global
    public bool AutoMerge { get; set; }

    public bool AutoRebase { get; set; }

    public string? AutoMergeWarning { get; set; }

    /// <summary>The ID of the merge queue this group is currently in, or null if not queued.</summary>
    public int? QueueId { get; set; }

    /// <summary>The 1-based position of this group within its queue, or null if not queued.</summary>
    public int? QueuePosition { get; set; }
    // ReSharper restore UnusedMember.Global
}