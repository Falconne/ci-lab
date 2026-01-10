namespace Bootstrap.Services.GitLab.Entities;

public sealed class GitLabProject
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? HttpUrlToRepo { get; set; }
}
