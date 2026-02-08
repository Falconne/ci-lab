namespace Bootstrap.Entities.Gitlab;

public sealed class GitlabUser
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public string? Name { get; set; }
}