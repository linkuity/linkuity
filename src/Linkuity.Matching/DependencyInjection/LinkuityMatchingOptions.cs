namespace Linkuity.Matching.DependencyInjection;

/// <summary>
/// Configures <see cref="MatchingServiceCollectionExtensions.AddLinkuityMatchingDefaults"/>.
/// Loaded profiles override built-ins of the same content type and add new ones,
/// using the same semantics as the CLI <c>--profiles</c> option.
/// </summary>
public sealed class LinkuityMatchingOptions
{
    private readonly List<string> _profilePaths = [];

    /// <summary>Paths (files or directories) to load matching profiles from.</summary>
    public IReadOnlyList<string> ProfilePaths => _profilePaths;

    /// <summary>
    /// Registers a JSON profile file or a directory of <c>*.profile.json</c> files
    /// to load at startup. May be called multiple times; paths accumulate.
    /// </summary>
    public LinkuityMatchingOptions LoadProfilesFrom(string fileOrDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileOrDirectory);
        _profilePaths.Add(fileOrDirectory);
        return this;
    }
}
