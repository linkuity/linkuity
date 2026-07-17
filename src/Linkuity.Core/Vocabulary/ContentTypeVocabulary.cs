namespace Linkuity.Core.Vocabulary;

// Maps recognized contentType values to canonical Neo4j type labels.
// Names are aligned with Schema.org classes (https://schema.org/Person, https://schema.org/Organization)
// but applied as bare property-graph labels. Adding a new contentType is an additive change here.
public static class ContentTypeVocabulary
{
    private static readonly IReadOnlyDictionary<string, string> Registry =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["person"] = "Person",
            ["organization"] = "Organization",
        };

    public static IReadOnlyCollection<string> AcceptedContentTypes => Registry.Keys.ToArray();

    public static bool TryGetLabel(string contentType, out string? label)
    {
        if (Registry.TryGetValue(contentType, out var found))
        {
            label = found;
            return true;
        }
        label = null;
        return false;
    }
}
