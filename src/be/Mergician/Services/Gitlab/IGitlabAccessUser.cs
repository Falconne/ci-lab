namespace Mergician.Services.Gitlab;

public interface IGitlabAccessUser
{
    Task<string?> GetValidAccessToken();
}
