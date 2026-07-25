using Linkuity.Core.Models;

namespace Linkuity.Matching.Canonicalization;

/// <summary>
/// The semantic-type → canonicalizer registrations shared by variant-consuming blocking
/// strategies (fingerprint, token). One map, one set of registrations: adding a taxonomy
/// (person/address/product) means registering its canonicalizer here and measuring with
/// the blocking audit — no strategy changes.
/// </summary>
internal static class TokenCanonicalizers
{
    public static readonly IReadOnlyDictionary<SemanticFieldType, ITokenCanonicalizer> Default =
        new Dictionary<SemanticFieldType, ITokenCanonicalizer>
        {
            [SemanticFieldType.OrganizationName] = new OrganizationNameCanonicalizer()
        };
}
