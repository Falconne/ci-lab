namespace Mergician.Entities.Database;

// TODO: Rename to BranchInProject and update any references in frontend.
public record BranchInProjectRecord
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