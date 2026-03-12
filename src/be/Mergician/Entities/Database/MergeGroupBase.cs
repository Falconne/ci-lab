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
    // ReSharper restore UnusedMember.Global
}