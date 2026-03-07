using Mergician.Entities;
using Mergician.Services.Authentication;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Detects and stores the GitLab server's timezone offset from UTC.
///     On startup, makes a lightweight API call using the service user account
///     to determine the server's timezone by inspecting the offset of returned timestamps.
///     This offset is used to adjust date-only parameters sent to the GitLab API
///     (e.g. the 'after' parameter in the events endpoint).
/// </summary>
public class GitLabTimezoneService
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<GitLabTimezoneService> _logger;

    private readonly GitlabUserFactory _userFactory;

    public GitLabTimezoneService(
        GitlabUserFactory userFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<GitLabTimezoneService> logger)
    {
        _userFactory = userFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    ///     The detected offset of the GitLab server from UTC.
    ///     Positive values mean ahead of UTC (e.g. UTC+5 = 5 hours).
    ///     Zero if detection has not run or the server uses UTC.
    /// </summary>
    public TimeSpan GitLabUtcOffset { get; private set; } = TimeSpan.Zero;

    /// <summary>
    ///     Adjusts a UTC date to the GitLab server's local date, for use in date-only
    ///     API parameters (e.g. the 'after' filter in the events endpoint).
    ///     Returns the date as it would appear on the GitLab server.
    /// </summary>
    public DateTimeOffset AdjustToGitLabLocal(DateTimeOffset utcTimestamp)
    {
        return utcTimestamp.ToOffset(GitLabUtcOffset);
    }

    /// <summary>
    ///     Detects the GitLab server's timezone by making a lightweight API call.
    ///     Uses the service user to call GET /api/v4/personal_access_tokens/self
    ///     and inspects the timezone offset of the created_at timestamp.
    /// </summary>
    public async Task DetectTimezone()
    {
        if (!_userFactory.IsServiceTokenConfigured)
        {
            _logger.LogError(
                "GitLab service token is not configured; assuming GitLab server uses UTC");

            return;
        }

        try
        {
            var serviceUser = _userFactory.GetServiceUser();
            var request = serviceUser.CreateRequest(HttpMethod.Get, "personal_access_tokens/self");
            var client = _httpClientFactory.CreateClient("GitLabOAuth");
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitLab timezone detection failed: GET personal_access_tokens/self returned {StatusCode}; assuming UTC",
                    (int)response.StatusCode);

                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenInfo = JsonSerializer.Deserialize<GitLabTokenSelfInfo>(json, _jsonOptions);

            if (tokenInfo?.CreatedAt == null)
            {
                _logger.LogWarning(
                    "GitLab timezone detection failed: could not parse created_at from token response; assuming UTC");

                return;
            }

            GitLabUtcOffset = tokenInfo.CreatedAt.Value.Offset;

            if (GitLabUtcOffset == TimeSpan.Zero)
            {
                _logger.LogInformation("GitLab server timezone detected: UTC");
            }
            else
            {
                _logger.LogInformation(
                    "GitLab server timezone detected: UTC{Offset:+hh\\:mm;-hh\\:mm}",
                    GitLabUtcOffset);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "GitLab timezone detection failed due to an error; assuming GitLab server uses UTC");
        }
    }
}
