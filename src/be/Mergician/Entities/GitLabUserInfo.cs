namespace Mergician.Entities;

public class GitLabUserInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string Email { get; set; } = "";
}
