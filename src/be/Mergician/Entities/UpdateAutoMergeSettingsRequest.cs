namespace Mergician.Entities;

/// <summary>
///     Request body for updating merge group auto merge/rebase settings.
/// </summary>
public class UpdateAutoMergeSettingsRequest
{
    public bool AutoMerge { get; set; }

    public bool AutoRebase { get; set; }
}
