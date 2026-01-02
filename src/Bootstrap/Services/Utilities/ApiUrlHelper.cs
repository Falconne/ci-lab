namespace Bootstrap.Services.Utilities;

public static class ApiUrlHelper
{
    public static string BuildUrl(string baseUrl, params string[] pathSegments)
    {
        var url = baseUrl.TrimEnd('/');
        foreach (var segment in pathSegments)
        {
            var trimmedSegment = segment.TrimStart('/');
            url = $"{url}/{trimmedSegment}";
        }

        return url;
    }

    public static string BuildGitLabApiUrl(string gitlabUrl, string endpoint)
    {
        return BuildUrl(gitlabUrl, "api/v4", endpoint);
    }

    public static string BuildTeamCityApiUrl(string teamcityUrl, string endpoint)
    {
        return BuildUrl(teamcityUrl, "app/rest", endpoint);
    }
}