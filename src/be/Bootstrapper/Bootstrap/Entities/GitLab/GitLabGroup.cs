namespace Bootstrap.Entities.Gitlab;

public class GitlabGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}
