namespace Linkuity.Matching.Profiles;

public interface IMatchingProfileProvider
{
    MatchingProfile GetProfile(string contentType);
    bool TryGetProfile(string contentType, out MatchingProfile? profile);
}
