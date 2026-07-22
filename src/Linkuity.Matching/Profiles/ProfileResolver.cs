using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Matching.Profiles;

/// <summary>
/// Resolves a batch <c>--profile</c> value to a <see cref="MatchingProfile"/> using the
/// same loader the durable path uses. A value is a built-in content-type name
/// (<c>person</c>, <c>organization</c>), a path to a <c>*.profile.json</c> file (CLI),
/// or raw profile JSON (API form field). Resolution never falls back silently: an
/// unresolvable value throws <see cref="MatchingProfileConfigException"/>.
/// </summary>
public static class ProfileResolver
{
    public static MatchingProfile ResolveNameOrFile(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (File.Exists(value))
            return new MatchingProfileConfigLoader().LoadFromFile(value, MatchingDefaults.CreateRegistry());

        if (TryBuiltIn(value, out var builtIn))
            return builtIn;

        throw new MatchingProfileConfigException(
            $"Profile '{value}' is not an existing file and is not a built-in profile. " +
            $"Built-in profiles: {BuiltInNames()}.");
    }

    public static MatchingProfile ResolveNameOrJson(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (TryBuiltIn(value, out var builtIn))
            return builtIn;

        return new MatchingProfileConfigLoader().LoadFromJson(value, MatchingDefaults.CreateRegistry());
    }

    private static bool TryBuiltIn(string name, out MatchingProfile profile)
    {
        profile = DefaultMatchingProfileProvider.BuiltInProfiles()
            .FirstOrDefault(p => string.Equals(p.ContentType, name, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    private static string BuiltInNames() =>
        string.Join(", ", DefaultMatchingProfileProvider.BuiltInProfiles().Select(p => p.ContentType));
}
