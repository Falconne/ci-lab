namespace Mergician.Entities.Database;

// TODO: Rename this to MergeGroupBase and make this the base class for `MergeGroup`. Update the usages of `MergeGroup` to use Id and Name instead of MergeGroupId and MergeGroupName.
public class MergeGroupRecord
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    // TODO: Remove LastUpdateTime property from here and from the merge group table in the database and references from this class. Only record the last update time against each branch rows that will be in `MergeGroup`.
    public DateTimeOffset LastUpdateTime { get; set; }
}