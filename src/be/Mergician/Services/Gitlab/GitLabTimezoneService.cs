using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Utilities;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Detects and stores the GitLab server's timezone offset from UTC.
///     Called by <see cref="Services.StartupService" /> during startup to determine the server's
///     timezone by inspecting the offset of returned timestamps.
///     This offset is used to adjust date-only parameters sent to the GitLab API
///     (e.g. the 'after' parameter in the events endpoint).
/// </summary>
public class GitLabTimezoneService
{
    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<GitLabTimezoneService> _logger;

    private readonly GitlabUserFactory _userFactory;

    public GitLabTimezoneService(
        GitlabUserFactory userFactory,
        GitLabApiClient gitLabApiClient,
        ILogger<GitLabTimezoneService> logger)
    {
        _userFactory = userFactory;
        _gitLabApiClient = gitLabApiClient;
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
    ///     Detects the GitLab server's timezone by calling GET /api/v4/personal_access_tokens/self.
    ///     Also acts as a GitLab connectivity health check. Throws on failure so the caller
    ///     can implement retry logic.
    /// </summary>
    public async Task DetectTimezone(CancellationToken cancellationToken = default)
    {
        var serviceUser = _userFactory.GetServiceUser();
        var tokenInfo = await _gitLabApiClient.ExecuteAsync(
            async (client, token) =>
            {
                using var request = serviceUser.CreateRequest(HttpMethod.Get, "personal_access_tokens/self");
                using var response = await client.SendAsync(request, token);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"GitLab timezone detection failed: GET personal_access_tokens/self returned {(int)response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync(token);
                var parsed = GitLabApiClient.DeserializeOrThrow<GitLabTokenSelfInfo>(
                    json,
                    _jsonOptions,
                    "DetectTimezone");

                if (parsed.CreatedAt == null)
                {
                    throw new JsonException(
                        "GitLab timezone detection failed: could not parse created_at from token response");
                }

                return parsed;
            },
            "DetectTimezone",
            GitLabApiFailureBehavior.Throw,
            cancellationToken);

        var createdAt = tokenInfo.CreatedAt
            ?? throw new JsonException(
                "GitLab timezone detection failed: created_at was null after successful parsing");

        GitLabUtcOffset = createdAt.Offset;

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
}