namespace Mergician.Entities;

/// <summary>
///     Cursor information returned by the backend to drive the next poll window.
/// </summary>
public record ActivityPollCursor(DateTimeOffset NextPollTime);
