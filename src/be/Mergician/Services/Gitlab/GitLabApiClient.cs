using Mergician.Services;
using System.Net;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitLabApiClient
{
    private static readonly TimeSpan[] _retryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(10)
    ];

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<GitLabApiClient> _logger;

    private readonly StartupStateService _startupStateService;

    public GitLabApiClient(
        IHttpClientFactory httpClientFactory,
        StartupStateService startupStateService,
        ILogger<GitLabApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _startupStateService = startupStateService;
        _logger = logger;
    }

    /// <summary>
    ///     Executes a GitLab API call with retry logic and JSON deserialization.
    ///     The <paramref name="requestFactory" /> is invoked once per attempt to produce a
    ///     fresh <see cref="HttpRequestMessage" /> (required because a sent message cannot be reused).
    ///     Non-retriable HTTP errors (4xx) throw <see cref="GitLabUnexpectedResponseException" />
    ///     immediately; connection failures and 5xx responses are retried with linear back-off.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        JsonSerializerOptions jsonOptions,
        string operationName,
        GitLabApiFailureBehavior failureBehavior,
        CancellationToken cancellationToken = default)
    {
        var (result, _) = await ExecuteCoreAsync<T>(
            requestFactory, jsonOptions, operationName, failureBehavior,
            captureNextPage: false, cancellationToken);
        return result!;
    }

    /// <summary>
    ///     Like <see cref="ExecuteAsync{T}" /> but also returns the value of the
    ///     <c>X-Next-Page</c> response header, for use with paginated GitLab endpoints.
    /// </summary>
    public async Task<(T Data, string? NextPage)> ExecutePagedAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        JsonSerializerOptions jsonOptions,
        string operationName,
        GitLabApiFailureBehavior failureBehavior,
        CancellationToken cancellationToken = default)
    {
        var (result, nextPage) = await ExecuteCoreAsync<T>(
            requestFactory, jsonOptions, operationName, failureBehavior,
            captureNextPage: true, cancellationToken);
        return (result!, nextPage);
    }

    private async Task<(T? Data, string? NextPage)> ExecuteCoreAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        JsonSerializerOptions jsonOptions,
        string operationName,
        GitLabApiFailureBehavior failureBehavior,
        bool captureNextPage,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        var totalAttempts = _retryDelays.Length + 1;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var client = _httpClientFactory.CreateClient("GitLabOAuth");
                using var request = requestFactory();
                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var responseException = new GitLabUnexpectedResponseException(
                        operationName, response.StatusCode, responseBody);

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
                    return (DeserializeOrThrow<T>(json, jsonOptions, operationName), nextPage);
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
            if (_startupStateService.IsInGitLabRecoveryMode)
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
        _startupStateService.EnterGitLabRecoveryMode();

        if (failureBehavior == GitLabApiFailureBehavior.EnterStartupMode)
        {
            throw new GitLabStartupRequiredException(operationName, failureException);
        }

        throw failureException;
    }

    private static T DeserializeOrThrow<T>(string json, JsonSerializerOptions jsonOptions, string operationName)
    {
        var result = JsonSerializer.Deserialize<T>(json, jsonOptions);
        if (result == null)
        {
            throw new JsonException(
                $"GitLab call '{operationName}' returned an empty or invalid JSON payload.");
        }

        return result;
    }

    private static bool IsRetriableStatusCode(HttpStatusCode statusCode)
    {
        return (int)statusCode >= 500;
    }
}