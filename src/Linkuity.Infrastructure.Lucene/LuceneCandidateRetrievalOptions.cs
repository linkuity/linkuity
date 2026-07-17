namespace Linkuity.Infrastructure.Lucene;

/// <summary>
/// Configuration for the Lucene candidate-retrieval adapter. The index lives at
/// <see cref="IndexDirectory"/> (a derived artifact, rebuildable from durable
/// records). <see cref="MaxCandidates"/> caps the Top-N returned per query; the
/// boost knobs weight the blocking-key / phonetic / fuzzy clauses for candidate
/// ordering only (never the match score).
/// </summary>
public sealed class LuceneCandidateRetrievalOptions
{
    public required string IndexDirectory { get; init; }
    public int MaxCandidates { get; init; } = 50;
    public float BlockingKeyBoost { get; init; } = 4f;
    public float PhoneticBoost { get; init; } = 2f;
    public float FuzzyBoost { get; init; } = 1f;
    public int FuzzyMaxEdits { get; init; } = 2;
}
