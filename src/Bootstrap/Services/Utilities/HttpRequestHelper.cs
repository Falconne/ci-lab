using System.Net.Http.Headers;
using System.Text;

namespace Bootstrap.Services.Utilities;

public static class HttpRequestHelper
{
    public static HttpRequestMessage CreateWithBearerAuth(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static HttpRequestMessage CreateWithBasicAuth(
        HttpMethod method,
        string url,
        string username,
        string password)
    {
        var request = new HttpRequestMessage(method, url);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        return request;
    }

    public static HttpRequestMessage CreateWithPrivateToken(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("PRIVATE-TOKEN", token);
        return request;
    }

    public static void AddJsonAccept(this HttpRequestMessage request)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static void SetJsonContent(this HttpRequestMessage request, string jsonBody)
    {
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
    }

    public static void SetXmlContent(this HttpRequestMessage request, string xmlBody)
    {
        request.Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml");
    }
}