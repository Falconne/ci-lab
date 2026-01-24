using System.Text.Json.Serialization;

namespace Bootstrap.Entities.TeamCity;

public sealed class TeamCityAgent
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
