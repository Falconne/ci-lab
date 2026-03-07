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

    public static T DeserializeOrThrow<T>(
        string json,
        JsonSerializerOptions jsonOptions,
        string operationName)
    {
        var result = JsonSerializer.Deserialize<T>(json, jsonOptions);
        if (result == null)
        {
            throw new JsonException(
                $"GitLab call '{operationName}' returned an empty or invalid JSON payload.");
        }

        return result;
    }

    public static bool IsRetriableStatusCode(HttpStatusCode statusCode)
    {
        return (int)statusCode >= 500;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<HttpClient, CancellationToken, Task<T>> operation,
        string operationName,
        GitLabApiFailureBehavior failureBehavior,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        var totalAttempts = _retryDelays.Length + 1;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var client = _httpClientFactory.CreateClient("GitLabOAuth");
                return await operation(client, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }
            catch (JsonException ex)
            {
                lastException = ex;
            }
            catch (GitLabUnexpectedResponseException ex)
            {
                lastException = ex;
            }

            if (lastException is GitLabUnexpectedResponseException responseException
                && !IsRetriableStatusCode(responseException.StatusCode))
            {
                throw lastException;
            }

            if (attempt == totalAttempts)
            {
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

        if (failureBehavior == GitLabApiFailureBehavior.EnterStartupMode)
        {
            _startupStateService.EnterGitLabRecoveryMode();
            throw new GitLabStartupRequiredException(operationName, failureException);
        }

        throw failureException;
    }
}