namespace Mergician.Entities.Database;

public class UserActivityRecord
{
    public int Id { get; set; }
    public int GitlabUserId { get; set; }
    public DateTimeOffset LastPollTimestamp { get; set; }
}
