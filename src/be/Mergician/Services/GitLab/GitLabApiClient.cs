using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Utilities;
using System.Net;
using System.Text.Json;

namespace Mergician.Services.GitLab;

/// <summary>
///     Central HTTP client for the GitLab API. Handles retry logic, JSON deserialization,
///     timezone detection, and health checks.
///     Timezone detection adjusts date-only API parameters to the GitLab server's local date.
///     Health checks probe GitLab until it becomes reachable, and are called by
///     <see cref="Services.StartupAndRecoveryService" /> during cold start and after recovery requests.
/// </summary>
public class GitLabApiClient
{
    private static readonly TimeSpan[] _retryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(10)
    ];

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _recoveryRetryDelay = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

    private readonly GitLabRecoveryService _gitLabRecoveryService;

    private readonly HealthService _healthService;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<GitLabApiClient> _logger;

    private readonly GitLabUserFactory _userFactory;

    public GitLabApiClient(
        IHttpClientFactory httpClientFactory,
        GitLabRecoveryService gitLabRecoveryService,
        HealthService healthService,
        GitLabUserFactory userFactory,
        ILogger<GitLabApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _gitLabRecoveryService = gitLabRecoveryService;
        _healthService = healthService;
        _userFactory = userFactory;
        _logger = logger;
    }

    /// <summary>
    ///     The detected offset of the GitLab server from UTC.
    ///     Positive values mean ahead of UTC (e.g. UTC+5 = 5 hours).
    ///     Zero if detection has not run or the server uses UTC.
    /// </summary>
    public TimeSpan GitLabUtcOffset { get; private set; } = TimeSpan.Zero;

    /// <summary>
    ///     Executes a GitLab API call with retry logic and JSON deserialization.
    ///     The <paramref name="requestFactory" /> is invoked once per attempt to produce a
    ///     fresh <see cref="HttpRequestMessage" /> (required because a sent message cannot be reused).
    ///     Non-retriable HTTP errors (4xx) throw <see cref="GitLabUnexpectedResponseException" />
    ///     immediately; connection failures and 5xx responses are retried with linear back-off.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        var (result, _) = await ExecuteCoreAsync<T>(
            requestFactory,
            false,
            cancellationToken);

        return result!;
    }

    /// <summary>
    ///     Like <see cref="ExecuteAsync{T}" /> but also returns the value of the
    ///     <c>X-Next-Page</c> response header, for use with paginated GitLab endpoints.
    /// </summary>
    public async Task<(T Data, string? NextPage)> ExecutePagedAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        var (result, nextPage) = await ExecuteCoreAsync<T>(
            requestFactory,
            true,
            cancellationToken);

        return (result!, nextPage);
    }

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
        var tokenInfo = await ExecuteAsync<GitLabTokenSelfInfo>(
            () => serviceUser.CreateRequest(HttpMethod.Get, "personal_access_tokens/self"),
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

    /// <summary>
    ///     Keeps probing GitLab until it becomes usable again. Recovery runs use a slower poll
    ///     interval than cold start so the app stays informative without hammering GitLab while
    ///     it is down.
    /// </summary>
    public async Task<bool> WaitForGitLabHealthy(
        bool isInRecoveryMode,
        CancellationToken cancellationToken)
    {
        var retryDelay = isInRecoveryMode ? _recoveryRetryDelay : _retryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            _healthService.SetStatus(false, "Checking GitLab...");

            try
            {
                _logger.LogInformation(
                    "GitLabApiClient: checking GitLab connectivity and detecting timezone{Suffix}",
                    isInRecoveryMode ? " during recovery" : string.Empty);

                await DetectTimezone(cancellationToken);
                _gitLabRecoveryService.ClearGitLabRecoveryMode();
                _logger.LogInformation("GitLabApiClient: GitLab check passed");
                return true;
            }
            catch (GitLabStartupRequiredException ex)
            {
                _logger.LogError(
                    ex,
                    "GitLabApiClient: GitLab check failed, will retry in {Delay}",
                    retryDelay);

                _healthService.SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error connecting to GitLab, please contact administrator.");

                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (GitLabUnexpectedResponseException ex)
            {
                _logger.LogError(
                    ex,
                    "GitLabApiClient: GitLab returned unexpected status {StatusCode}, will retry in {Delay}",
                    (int)ex.StatusCode,
                    retryDelay);

                _healthService.SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error connecting to GitLab, please contact administrator.");

                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    ///     Shared retry loop for all GitLab calls. Once a runtime failure proves GitLab is down,
    ///     this method flips the app into recovery mode so middleware can surface the recovery
    ///     overlay and sibling requests can stop retrying immediately.
    /// </summary>
    private async Task<(T? Data, string? NextPage)> ExecuteCoreAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        bool captureNextPage,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        var totalAttempts = _retryDelays.Length + 1;
        string? operationName = null;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var client = _httpClientFactory.CreateClient("GitLabOAuth");
                using var request = requestFactory();
                operationName ??= GenerateOperationName(request);

                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var responseException = new GitLabUnexpectedResponseException(
                        operationName,
                        response.StatusCode,
                        responseBody);

                    if (!IsRetriableStatusCode(response.StatusCode))
                    {
                        // Non-retriable (4xx etc.): propagate immediately, bypass retry logic.
                        throw responseException;
                    }

                    // Retriable (5xx): fall through to retry delay below.
                    lastException = responseException;
                }
                else
                {
                    string? nextPage = null;
                    if (captureNextPage)
                    {
                        nextPage = response.Headers.TryGetValues("X-Next-Page", out var nextPageValues)
                            ? nextPageValues.FirstOrDefault()
                            : null;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    return (DeserializeOrThrow<T>(json, operationName), nextPage);
                }
            }
            catch (GitLabUnexpectedResponseException)
            {
                // Non-retriable HTTP error — re-throw immediately without retry.
                throw;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }

            if (attempt == totalAttempts)
            {
                break;
            }

            // If another thread already triggered GitLab recovery mode, stop retrying
            // immediately so we don't waste time hitting an unreachable GitLab instance.
            if (_gitLabRecoveryService.IsInGitLabRecoveryMode)
            {
                _logger.LogInformation(
                    "GitLab call {OperationName} aborting retries: GitLab recovery mode is active",
                    operationName);

                break;
            }

            var delay = _retryDelays[attempt - 1];
            _logger.LogError(
                lastException,
                "GitLab call {OperationName} failed on attempt {Attempt}/{TotalAttempts}; retrying in {Delay}",
                operationName,
                attempt,
                totalAttempts,
                delay);

            await Task.Delay(delay, cancellationToken);
        }

        operationName ??= "Unknown";

        var failureException = new GitLabApiFailureException(
            operationName,
            totalAttempts,
            lastException ?? new InvalidOperationException("GitLab call failed without an exception."));

        _logger.LogError(
            failureException,
            "GitLab call {OperationName} failed after {TotalAttempts} attempts",
            operationName,
            totalAttempts);

        // Always enter recovery mode when retries are exhausted, regardless of the
        // caller's failure behavior. This ensures the frontend shows the GitLab error
        // overlay and other threads stop wasting time retrying against an unreachable
        // GitLab instance.
        _gitLabRecoveryService.EnterGitLabRecoveryMode();

        throw new GitLabStartupRequiredException(operationName, failureException);
    }

    /// <summary>
    ///     Generates a human-readable operation name from an HTTP request for use in logging
    ///     and exception messages. Format: "GET projects/123/merge_requests/456".
    /// </summary>
    private static string GenerateOperationName(HttpRequestMessage request)
    {
        var method = request.Method.Method;
        var path = request.RequestUri?.PathAndQuery ?? "unknown";

        // Strip the /api/v4/ prefix if present
        var apiPrefix = "/api/v4/";
        var apiIndex = path.IndexOf(apiPrefix, StringComparison.OrdinalIgnoreCase);
        if (apiIndex >= 0)
        {
            path = path[(apiIndex + apiPrefix.Length)..];
        }

        // Strip query string for cleaner names
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        return $"{method} {path}";
    }

    /// <summary>
    ///     Deserializes a successful GitLab response and treats null payloads as contract
    ///     violations so callers do not continue with partially valid state.
    /// </summary>
    private static T DeserializeOrThrow<T>(
        string json,
        string operationName)
    {
        var result = JsonSerializer.Deserialize<T>(json, _jsonOptions);
        if (result == null)
        {
            throw new JsonException(
                $"GitLab call '{operationName}' returned an empty or invalid JSON payload.");
        }

        return result;
    }

    /// <summary>
    ///     Only server-side failures are retried. Client-side failures are surfaced immediately
    ///     because they usually represent a bad request or bad credentials rather than recovery.
    /// </summary>
    private static bool IsRetriableStatusCode(HttpStatusCode statusCode)
    {
        return (int)statusCode >= 500;
    }
}