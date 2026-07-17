namespace Linkuity.Matching.Profiles.Configuration;

/// <summary>Thrown when a JSON matching-profile document is missing required
/// values or references a strategy, evaluator, semantic type, or role that does
/// not exist. The message names the offending value and the config source.</summary>
public sealed class MatchingProfileConfigException : Exception
{
    public MatchingProfileConfigException(string message) : base(message) { }
    public MatchingProfileConfigException(string message, Exception innerException) : base(message, innerException) { }
}
