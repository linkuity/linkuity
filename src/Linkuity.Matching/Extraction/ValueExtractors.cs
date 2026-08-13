namespace Linkuity.Matching.Extraction;

/// <summary>
/// The name → extractor registrations a profile's <c>extractor</c> property resolves against.
/// One map, one set of registrations, mirroring
/// <see cref="Canonicalization.TokenCanonicalizers"/>: adding an extractor means registering it
/// here, and nothing else in the engine changes.
///
/// Deliberately a static map rather than a DI-resolved strategy set. Extraction is a pure
/// value-to-value function with no dependencies, like canonicalization and unlike the pluggable
/// blocking/scoring strategies; the profile loader needs to validate a name at load time without
/// a service provider in hand.
/// </summary>
public static class ValueExtractors
{
    public static readonly IReadOnlyDictionary<string, IValueExtractor> Default =
        new Dictionary<string, IValueExtractor>(StringComparer.Ordinal)
        {
            ["org-legal-form"] = new OrganizationLegalFormExtractor()
        };
}
