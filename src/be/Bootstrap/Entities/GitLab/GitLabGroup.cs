namespace Bootstrap.Entities.GitLab;

public class GitLabGroup
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;
}