namespace Linkuity.Core.Models;

/// <summary>
/// One field's contribution to a match score, persisted for audit/explainability.
/// Mirrors the engine's ScoreContribution but lives in Core so durable models do
/// not depend on the matching engine. Signal is the field name; Value is the raw
/// per-field similarity in [0,1]; Weight is the profile-configured field weight;
/// Contribution is this field's weighted share of the final score.
/// </summary>
public sealed record MatchScoreFactor(string Signal, double Value, double Weight, double Contribution);
