namespace Bootstrap.Entities.TeamCity;

public class TeamCityTokensResponse
{
    public int? Count { get; set; }

    public List<TeamCityToken>? Token { get; set; }
}

public class TeamCityToken
{
    public string? Name { get; set; }

    public string? Value { get; set; }

    public string? CreationTime { get; set; }

    public string? ExpirationTime { get; set; }
}