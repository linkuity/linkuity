using System.Net;
using System.Net.Http.Headers;

namespace Linkuity.Api.Tests.Endpoints;

public sealed class RunEndpointEvidenceScaleTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public RunEndpointEvidenceScaleTests(TestWebApplicationFactory factory) => _factory = factory;

    // Same fixture shape as MatchingProfileConfigLoaderTests.EvidenceScoringOrganizationJson:
    // scoringStrategy "evidence" produces ScoreScale.LogOdds, so its autoMatchThreshold/
    // reviewThreshold (8.0 / 4.0) sit well outside [0,1]. BatchMatchingService.BuildMatchesCsv
    // always builds MatchThresholds on the default ScoreScale.UnitInterval, so without the
    // RequireLiveMatchingScale guard in RunEndpoints this profile loads fine and then throws an
    // unhandled ArgumentOutOfRangeException on the first scored pair.
    private const string EvidenceScoringOrganizationJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Matchable", "Identifier"], "weight": 1.0, "evidence": { "sameEntityAgreement": 0.9, "chanceAgreement": 0.1 } }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "evidence",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 8.0,
      "reviewThreshold": 4.0
    }
    """;

    [Fact]
    public async Task PostRun_EvidenceScoredProfile_Returns400NotServerError()
    {
        var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(EvidenceScoringOrganizationJson), "profile");

        const string csv = "source,organization_name\nCRM,Acme Corp\n";
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(content, "file", "sample.csv");

        var response = await client.PostAsync("/run", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid profile", body, StringComparison.Ordinal);
        Assert.Contains("LogOdds", body, StringComparison.Ordinal);
    }
}
