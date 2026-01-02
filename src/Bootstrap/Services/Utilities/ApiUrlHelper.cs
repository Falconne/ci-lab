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

}