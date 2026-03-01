namespace Mergician.Entities.Database;

public record BranchInProjectRecord
{
    public int Id { get; set; }
    public string BranchName { get; set; } = "";
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public string ProjectNameWithNamespace { get; set; } = "";
}
