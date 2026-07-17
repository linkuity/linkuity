using System.Net;

namespace Linkuity.Api.Tests.Endpoints;

public class HealthEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HealthEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_Health_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
