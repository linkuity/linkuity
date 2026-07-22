using System.Net;
using System.Net.Http.Headers;

namespace Linkuity.Api.Tests.Endpoints;

public sealed class RunEndpointLimitTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public RunEndpointLimitTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PostRun_OversizeInput_Returns400WithGuidance()
    {
        var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("person"), "profile");
        var big = new byte[Linkuity.Api.Endpoints.RunEndpoints.MaxInputBytes + 1];
        var csv = new ByteArrayContent(big);
        csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csv, "file", "big.csv");

        var response = await client.PostAsync("/run", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("synchronous limit", await response.Content.ReadAsStringAsync());
    }
}
