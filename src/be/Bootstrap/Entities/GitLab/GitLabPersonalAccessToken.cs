namespace Bootstrap.Entities.GitLab;

public sealed class GitLabPersonalAccessToken
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Token { get; set; }
}
