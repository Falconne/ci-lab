namespace Mergician.Entities.Database;

public class BranchInProjectRecord
{
    public int Id { get; set; }
    public string BranchName { get; set; } = "";
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
}
