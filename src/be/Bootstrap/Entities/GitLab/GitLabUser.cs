namespace Bootstrap.Entities.GitLab;

public sealed class GitLabUser
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public string? Name { get; set; }
}