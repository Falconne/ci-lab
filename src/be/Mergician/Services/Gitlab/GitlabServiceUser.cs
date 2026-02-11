using Mergician.Entities;
using Serilog;

namespace Mergician.Services.Gitlab;

public class GitlabServiceUser : IGitlabAccessUser
{
    private readonly MergicianSettings _settings;

    public GitlabServiceUser(MergicianSettings settings)
    {
        _settings = settings;
    }

    public Task<string?> GetValidAccessToken()
    {
        var token = _settings.GitLab.ServiceToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warning("GitLab service token is not configured");
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(token);
    }
}
