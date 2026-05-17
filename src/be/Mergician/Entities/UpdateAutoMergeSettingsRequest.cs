namespace Mergician.Entities;

/// <summary>
///     Request body for updating merge group auto merge settings.
/// </summary>
public class UpdateAutoMergeSettingsRequest
{
    public bool AutoMerge { get; set; }
}