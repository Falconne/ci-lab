namespace Mergician.Entities.Database;

public record BranchInProject
{
    public int Id { get; set; }

    public string BranchName { get; set; } = "";

    public int ProjectId { get; set; }

    // Used by the frontend
    // ReSharper disable once UnusedMember.Global
    public string ProjectName { get; set; } = "";

    // Used by the frontend
    // ReSharper disable once UnusedMember.Global
    public string ProjectNameWithNamespace { get; set; } = "";

    // TODO: Also store the project URL here (and in the database). Update the code to pass this in
    // when creating BranchInProject instance and saving it to the DB. When a `BranchInProject` entry
    // is fetched from the DB later in the code, if there is code that is using the Gitlab API to fetch
    // the project URL, update it to use the stored URL instead.
}