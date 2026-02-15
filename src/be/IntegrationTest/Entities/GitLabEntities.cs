using System.Text.Json.Serialization;

namespace IntegrationTest.Entities;

public record GitLabProjectInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public record GitLabMrInfo(
    [property: JsonPropertyName("iid")] int Iid);
