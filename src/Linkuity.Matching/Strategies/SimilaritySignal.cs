namespace Linkuity.Matching.Strategies;

/// <summary>A single raw similarity signal between two records (e.g. "exact:email" = 1.0).</summary>
public sealed record SimilaritySignal(string Name, double Value);
