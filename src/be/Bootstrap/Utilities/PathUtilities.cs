using Serilog;

namespace Bootstrap.Utilities;

public static class PathUtilities
{
    /// <summary>
    ///     Searches upward from <see cref="AppContext.BaseDirectory" /> for a 'data' directory
    ///     containing a '.placeholder' file. Returns the parent directory (repository root).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no matching directory is found.</exception>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            var dataDir = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(dataDir) && File.Exists(Path.Combine(dataDir, ".placeholder")))
            {
                Log.Debug("Found repository root at {RepoRoot}", dir.FullName);
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root. Expected a 'data' directory containing '.placeholder' "
            + $"in an ancestor of '{AppContext.BaseDirectory}'.");
    }
}