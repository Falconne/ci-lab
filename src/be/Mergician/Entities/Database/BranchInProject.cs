namespace Mergician.Entities.Database;

public record BranchInProject
{
    public int Id { get; set; }

    public string BranchName { get; set; } = "";

    public int ProjectId { get; set; }

    // Used by the frontend
    // ReSharper disable once UnusedMember.Global
    public string ProjectName { get; set; } = "";

    // Used by the frontend
    // ReSharper disable once UnusedMember.Global
    public string ProjectNameWithNamespace { get; set; } = "";
}