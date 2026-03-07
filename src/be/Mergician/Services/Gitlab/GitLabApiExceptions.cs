using System.Net;

namespace Mergician.Services.Gitlab;

public enum GitLabApiFailureBehavior
{
    Throw,
    EnterStartupMode
}

public class GitLabApiFailureException : Exception
{
    public GitLabApiFailureException(string operationName, int totalAttempts, Exception innerException)
        : base(
            $"GitLab call '{operationName}' failed after {totalAttempts} attempts.",
            innerException)
    {
        OperationName = operationName;
        TotalAttempts = totalAttempts;
    }

    public string OperationName { get; }

    public int TotalAttempts { get; }
}

public class GitLabStartupRequiredException : Exception
{
    public GitLabStartupRequiredException(string operationName, Exception innerException)
        : base($"GitLab call '{operationName}' requires startup recovery.", innerException)
    {
        OperationName = operationName;
    }

    public string OperationName { get; }
}

public class GitLabUnexpectedResponseException : Exception
{
    public GitLabUnexpectedResponseException(
        string operationName,
        HttpStatusCode statusCode,
        string? responseBody = null)
        : base($"GitLab call '{operationName}' returned unexpected status {(int)statusCode}.")
    {
        OperationName = operationName;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public string OperationName { get; }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The raw response body, if it was read before the exception was thrown.</summary>
    public string? ResponseBody { get; }
}