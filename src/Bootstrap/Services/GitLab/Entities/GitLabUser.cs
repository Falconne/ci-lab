namespace Bootstrap.Services.GitLab.Entities;

public sealed class GitLabUser
{
    public int Id { get; set; }
    public string? Username { get; set; }
    public string? Name { get; set; }
}
