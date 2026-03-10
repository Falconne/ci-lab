namespace Mergician.Services.Authentication;

public static class CookieSecurity
{
    public static bool ShouldUseSecureCookies(HttpRequest request)
    {
        if (request.IsHttps)
        {
            return true;
        }

        if (!request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProtoValues))
        {
            return false;
        }

        foreach (var value in forwardedProtoValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var segment in value.Split(
                         ',',
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(segment, "https", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}