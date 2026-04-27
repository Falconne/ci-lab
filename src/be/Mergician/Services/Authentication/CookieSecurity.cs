using Util;

namespace Mergician.Services.Authentication;

public static class CookieSecurity
{
    /// <summary>
    ///     Returns true if cookies should use the Secure flag. Checks for HTTPS directly
    ///     or via a trusted X-Forwarded-Proto header from a reverse proxy.
    ///     Note: trusting X-Forwarded-Proto unconditionally is safe for internal deployments
    ///     behind a known proxy, but would be unsafe if the application were exposed directly
    ///     to the internet. If that changes, restrict forwarded header trust to specific proxy IPs
    ///     (e.g., via ForwardedHeadersOptions.KnownProxies in ASP.NET Core).
    /// </summary>
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
            if (value.IsEmpty())
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