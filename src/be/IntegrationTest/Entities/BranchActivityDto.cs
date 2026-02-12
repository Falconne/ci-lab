namespace IntegrationTest.Entities;

public class BranchActivityDto
{
    public string BranchName { get; set; } = "";
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public bool HasMergeRequest { get; set; }
    public int? ApprovalsRequired { get; set; }
    public int? ApprovalsGiven { get; set; }
}
