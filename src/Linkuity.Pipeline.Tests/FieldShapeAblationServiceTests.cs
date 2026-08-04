using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Field-shape ablation: a width narrows which fields are Matchable and must never touch
/// Blocking (see FieldShapeAblationService class doc). These tests pin two things: the role
/// bookkeeping in <see cref="FieldShapeAblationService.BuildWidthProfile"/>, and the
/// per-width numbers <see cref="FieldShapeAblationService.Audit"/> reports, on a corpus small
/// enough to hand-verify.
/// <para>
/// Fixture: two fields, "b" (LastName, Matchable+Blocking, weight 1, exact) and "d" (FirstName,
/// Matchable only, weight 1, exact). Three records: r1/r2 share both b and d (a true pair, group
/// G1); r3 shares b but not d (a false pair against both, group G2, singleton so no true pairs
/// of its own). All three land in the same blocking block via "b", so reachability is 100% and
/// candidate generation never depends on which width is under test.
/// </para>
/// <para>
/// Under width "b" (d not matchable): every pair scores 1.0 (b matches for all three), so the
/// true pair and both false pairs are tied at the only observed cut — 100% precision is
/// unreachable at any threshold. Under width "b+d": the true pair scores 1.0 and both false
/// pairs score 0.5, so cutting at 1.0 achieves exact 100% precision at 100% recall. Same
/// candidate set, same records, different Matchable width — a different usable threshold. That
/// contrast is the instrument's entire reason to exist.
/// </para>
/// </summary>
public class FieldShapeAblationServiceTests
{
    private static MatchingProfile BaseProfile(string scoringStrategy = "weighted") => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "b", SemanticType = SemanticFieldType.LastName,
                Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "d", SemanticType = SemanticFieldType.FirstName,
                Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = scoringStrategy,
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.5,
        ReviewThreshold = 0.3
    };

    private static EntityRecord Rec(string id, string b, string d) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["b"] = b, ["d"] = d },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static readonly IReadOnlyList<EntityRecord> Records =
    [
        Rec("r1", "Smith", "1"),
        Rec("r2", "Smith", "1"), // true pair with r1 (group G1)
        Rec("r3", "Smith", "2")  // shares blocking key "b", but a different (singleton) group
    ];

    private static readonly Dictionary<string, string> Truth = new()
    {
        ["r1"] = "G1", ["r2"] = "G1", ["r3"] = "G2"
    };

    private static FieldShapeAblationService NewService() => new(MatchingDefaults.CreateRegistry());

    // ---- BuildWidthProfile: role bookkeeping ----

    [Fact]
    public void BuildWidthProfile_StripsMatchable_ButNeverBlocking()
    {
        var width = new FieldWidth("d-only", ["d"]);
        var narrowed = FieldShapeAblationService.BuildWidthProfile(BaseProfile(), width);

        var b = narrowed.Fields.Single(f => f.Name == "b");
        var d = narrowed.Fields.Single(f => f.Name == "d");

        Assert.False(b.Roles.HasFlag(FieldRole.Matchable));
        Assert.True(b.Roles.HasFlag(FieldRole.Blocking)); // untouched, even though not in this width
        Assert.True(d.Roles.HasFlag(FieldRole.Matchable));
    }

    [Fact]
    public void BuildWidthProfile_FullWidth_LeavesBothFieldsMatchable()
    {
        var width = new FieldWidth("full", ["b", "d"]);
        var narrowed = FieldShapeAblationService.BuildWidthProfile(BaseProfile(), width);

        Assert.All(narrowed.Fields, f => Assert.True(f.Roles.HasFlag(FieldRole.Matchable)));
        Assert.True(narrowed.Fields.Single(f => f.Name == "b").Roles.HasFlag(FieldRole.Blocking));
    }

    [Fact]
    public void BuildWidthProfile_NamingAFieldNotMatchableInBase_IsRefused()
    {
        // "e" carries no Matchable role in the base profile: a width may only narrow the base
        // profile's Matchable set, never invent evidence the base profile did not configure.
        var profile = BaseProfile() with
        {
            Fields =
            [
                .. BaseProfile().Fields,
                new ProfileField { Name = "e", SemanticType = SemanticFieldType.AddressLine,
                    Roles = FieldRole.Searchable, Weight = 1.0 }
            ]
        };
        var width = new FieldWidth("bad", ["e"]);

        var ex = Assert.Throws<ArgumentException>(() => FieldShapeAblationService.BuildWidthProfile(profile, width));
        Assert.Contains("e", ex.Message);
        Assert.Contains("does not mark", ex.Message);
    }

    [Fact]
    public void BuildWidthProfile_NamingAnUnknownField_IsRefused()
    {
        var width = new FieldWidth("bad", ["nope"]);
        var ex = Assert.Throws<ArgumentException>(() => FieldShapeAblationService.BuildWidthProfile(BaseProfile(), width));
        Assert.Contains("nope", ex.Message);
    }

    // ---- Audit(): input validation ----

    [Fact]
    public void Audit_NoWidths_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            NewService().Audit(Records, BaseProfile(), Truth, []));
        Assert.Contains("width", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_DuplicateWidthNames_Throws()
    {
        var widths = new[] { new FieldWidth("x", ["b"]), new FieldWidth("x", ["b", "d"]) };
        var ex = Assert.Throws<ArgumentException>(() =>
            NewService().Audit(Records, BaseProfile(), Truth, widths));
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void Audit_WidthWithNoFields_Throws()
    {
        var widths = new[] { new FieldWidth("empty", []) };
        var ex = Assert.Throws<ArgumentException>(() =>
            NewService().Audit(Records, BaseProfile(), Truth, widths));
        Assert.Contains("empty", ex.Message);
    }

    // ---- Audit(): the numbers, hand-verified per the class doc above ----

    [Fact]
    public void Audit_NarrowWidth_HasNoThresholdReaching100PercentPrecision()
    {
        var widths = new[] { new FieldWidth("b", ["b"]) };
        var result = NewService().Audit(Records, BaseProfile(), Truth, widths);

        var row = Assert.Single(result.Rows);
        Assert.Equal(1, row.MatchableFieldCount);
        Assert.Equal(1, row.TruePairs);       // only r1-r2
        Assert.Equal(1.0, row.Reachability, 6); // all three share the "b" blocking key

        Assert.False(row.PerfectPrecisionReachable);
        Assert.Null(row.ThresholdAt100Precision);
        Assert.Null(row.RecallAt100Precision);

        // At the only observed cut (1.0) all three candidate pairs tie, so precision caps at 1/3.
        Assert.NotNull(row.MaxPrecisionObserved);
        Assert.Equal(1.0 / 3.0, row.MaxPrecisionObserved!.Value, 6);
    }

    [Fact]
    public void Audit_WiderWidth_ReachesExact100PercentPrecision_AtFullRecall()
    {
        var widths = new[] { new FieldWidth("b+d", ["b", "d"]) };
        var result = NewService().Audit(Records, BaseProfile(), Truth, widths);

        var row = Assert.Single(result.Rows);
        Assert.Equal(2, row.MatchableFieldCount);
        Assert.True(row.PerfectPrecisionReachable);
        Assert.Equal(1.0, row.ThresholdAt100Precision!.Value, 6);
        Assert.Equal(1.0, row.RecallAt100Precision!.Value, 6);
    }

    [Fact]
    public void Audit_SameCandidateSet_AcrossWidths_OnlyScoringInputVaries()
    {
        // The whole point of ablating Matchable-only: reachability (a function of blocking,
        // never touched) must be identical across widths even though their thresholds differ.
        var widths = new[] { new FieldWidth("b", ["b"]), new FieldWidth("b+d", ["b", "d"]) };
        var result = NewService().Audit(Records, BaseProfile(), Truth, widths);

        Assert.All(result.Rows, r => Assert.Equal(1.0, r.Reachability, 6));
        Assert.All(result.Rows, r => Assert.Equal(1, r.TruePairs));

        // And the usable threshold DOES move: unreachable at width "b", 1.0 at width "b+d".
        var narrow = result.Rows.Single(r => r.WidthName == "b");
        var wide = result.Rows.Single(r => r.WidthName == "b+d");
        Assert.False(narrow.PerfectPrecisionReachable);
        Assert.True(wide.PerfectPrecisionReachable);
    }
}
