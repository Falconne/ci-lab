using System.Text.Json;
using System.Xml.Linq;

namespace Bootstrap.Services.Utilities;

public static class ResponseParser
{
    public static string? TryParseTokenFromResponse(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        // Try JSON parsing
        var token = TryParseJsonToken(responseBody);
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        // Try XML parsing
        token = TryParseXmlToken(responseBody);
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        // If response is reasonably long, assume it's the token itself
        if (responseBody.Length > 20 && responseBody.Length < 500)
        {
            return responseBody.Trim();
        }

        return null;
    }

    public static string? TryParseJsonToken(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            // Try common JSON field names for tokens
            var fieldNames = new[] { "token", "value", "tokenValue", "access_token", "accessToken" };
            foreach (var fieldName in fieldNames)
            {
                if (root.TryGetProperty(fieldName, out var element))
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON, continue to other parsing attempts
        }

        return null;
    }

    public static string? TryParseXmlToken(string xmlBody)
    {
        try
        {
            var xmlDoc = XDocument.Parse(xmlBody);
            var tokenElement = xmlDoc.Root?.Element("token") ?? xmlDoc.Root;

            if (tokenElement != null)
            {
                // Try common XML attribute names
                var attributeNames = new[] { "value", "tokenValue", "token" };
                foreach (var attrName in attributeNames)
                {
                    var attr = tokenElement.Attribute(attrName);
                    if (attr != null && !string.IsNullOrWhiteSpace(attr.Value))
                    {
                        return attr.Value;
                    }
                }

                // Try element value
                if (!string.IsNullOrWhiteSpace(tokenElement.Value))
                {
                    return tokenElement.Value.Trim();
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // Not valid XML, return null
        }

        return null;
    }
}