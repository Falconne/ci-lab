using Serilog;

namespace Mergician.Services.Gitlab;

public class GitlabServiceUser : GitlabAccessUserBase
{
    private readonly string? _serviceToken;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_serviceToken);

    public GitlabServiceUser(string? serviceToken, string apiBaseUrl)
        : base(apiBaseUrl)
    {
        _serviceToken = serviceToken;

        if (!IsConfigured)
            Log.Error("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting");
        else
            Log.Information("GitLab service user initialised with a configured service token");
    }

    public override Task<string?> GetValidAccessToken()
    {
        if (!IsConfigured)
        {
            Log.Error("GitLab service token is not configured — cannot authenticate API request");
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(_serviceToken);
    }
}
