namespace Mergician.Entities.Database;

public class MergeGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset LastUpdateTime { get; set; }
}
