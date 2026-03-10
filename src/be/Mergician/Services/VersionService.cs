using System.Reflection;

namespace Mergician.Services;

/// <summary>
///     Provides version information for the backend.
///     The version is set from the git hash during build time via the InformationalVersion property.
/// </summary>
public class VersionService
{
    private readonly string _version;

    public VersionService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var versionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        _version = versionAttribute?.InformationalVersion ?? "unknown";
    }

    public string GetVersion()
    {
        return _version;
    }
}