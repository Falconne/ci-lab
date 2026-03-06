namespace Mergician.Entities.Database;

// TODO: Rename to BranchInProject and update any references in frontend.
public record BranchInProjectRecord
{
    public int Id { get; set; }

    public string BranchName { get; set; } = "";

    public int ProjectId { get; set; }

    public string ProjectName { get; set; } = "";

    public string ProjectNameWithNamespace { get; set; } = "";
}