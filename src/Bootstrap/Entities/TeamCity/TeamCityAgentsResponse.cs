using System.Text.Json.Serialization;

namespace Bootstrap.Entities.TeamCity;

public sealed class TeamCityAgentsResponse
{
    [JsonPropertyName("agent")]
    public TeamCityAgent[]? Agent { get; set; }
}
