using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// The durable resolver rebuilds the caller's profile before handing it to the engine
/// (retrieval strategy and thresholds come from the request, not the profile file). Every
/// other setting must survive that rebuild untouched — a dropped setting is invisible,
/// because the engine simply behaves as if it were never configured.
///
/// These tests use the built-in organization profile unmodified, so they assert the
/// shipped default configuration rather than a test-only construction.
/// </summary>
public class IncrementalResolverProfileFidelityTests
{
    private static readonly MatchingProfile OrganizationProfile =
        DefaultMatchingProfileProvider.CreateOrganizationProfile();

    private static IncrementalResolver NewResolver()
        => new(MatchingDefaults.CreateEngine(), hasIndex: false, new CohesionClusterMergePolicy());

    private static EntityRecord Keyed(
        IncrementalResolver resolver, Guid projectId, Guid sourceId, Guid batchId,
        string srid, Dictionary<string, string> fields, DateTimeOffset at)
    {
        var record = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = srid,
            Fields = fields,
            BlockingKeys = [],
            CreatedAt = at
        };

        return new EntityRecord
        {
            Id = record.Id,
            ProjectId = record.ProjectId,
            SourceId = record.SourceId,
            IngestBatchId = record.IngestBatchId,
            SourceRecordId = record.SourceRecordId,
            Fields = record.Fields,
            BlockingKeys = resolver.GenerateBlockingKeys(record, OrganizationProfile),
            CreatedAt = record.CreatedAt
        };
    }

    /// <summary>
    /// The built-in organization profile sets MaxBlockSize = 50. Fifty-one identical existing
    /// records put every one of the incoming record's blocking keys over that threshold, so
    /// suppression must discard them all and the record must retrieve nothing.
    ///
    /// Identical records are the point: a partially-shared block would leave some rare key
    /// active and retrieve through it, which would not exercise suppression.
    /// </summary>
    [Fact]
    public void ProfileMaxBlockSize_SurvivesTheCallRebuild_OversizedBlockRetrievesNothing()
    {
        Assert.Equal(50, OrganizationProfile.MaxBlockSize);

        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();

        Dictionary<string, string> Fields() => new()
        {
            ["source"] = "CRM",
            ["organization_name"] = "Contoso Holdings",
            ["email"] = "shared@contoso.example"
        };

        var context = new InMemoryResolutionContext();
        for (var i = 0; i < 51; i++)
        {
            var existing = Keyed(resolver, projectId, sourceId, seedBatch, $"crm-{i:000}", Fields(), now);
            context.Records.Add(existing);
            context.Clusters.Add(new Cluster
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                MemberEntityRecordIds = [existing.Id],
                CreatedAt = now
            });
        }

        var incoming = Keyed(resolver, projectId, sourceId, incBatch, "web-001", Fields(), now.AddMinutes(1));
        var request = new IncrementalIngestRequest(projectId, sourceId, incBatch, [incoming], 0.90, 0.75);
        var project = new Project { Id = projectId, Name = "MDM", ContentType = "organization", CreatedAt = now };

        var (result, _) = resolver.Resolve(
            request, project, OrganizationProfile, [incoming], context, now.AddMinutes(1));

        Assert.Equal(0, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(1, result.SingletonClusters);
    }

    /// <summary>
    /// The same corpus one record smaller leaves every block exactly at the threshold, which
    /// stays active. Without this, the test above would also pass if suppression were
    /// discarding everything unconditionally.
    /// </summary>
    [Fact]
    public void ProfileMaxBlockSize_BlockExactlyAtThreshold_StillRetrieves()
    {
        var resolver = NewResolver();
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var seedBatch = Guid.NewGuid();
        var incBatch = Guid.NewGuid();

        Dictionary<string, string> Fields() => new()
        {
            ["source"] = "CRM",
            ["organization_name"] = "Contoso Holdings",
            ["email"] = "shared@contoso.example"
        };

        var context = new InMemoryResolutionContext();
        for (var i = 0; i < 50; i++)
        {
            var existing = Keyed(resolver, projectId, sourceId, seedBatch, $"crm-{i:000}", Fields(), now);
            context.Records.Add(existing);
            context.Clusters.Add(new Cluster
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                MemberEntityRecordIds = [existing.Id],
                CreatedAt = now
            });
        }

        var incoming = Keyed(resolver, projectId, sourceId, incBatch, "web-001", Fields(), now.AddMinutes(1));
        var request = new IncrementalIngestRequest(projectId, sourceId, incBatch, [incoming], 0.90, 0.75);
        var project = new Project { Id = projectId, Name = "MDM", ContentType = "organization", CreatedAt = now };

        var (result, _) = resolver.Resolve(
            request, project, OrganizationProfile, [incoming], context, now.AddMinutes(1));

        // One auto-match edge per existing duplicate: AutoMatches counts edges, not records.
        Assert.Equal(0, result.SingletonClusters);
        Assert.Equal(50, result.AutoMatches);
    }
}
