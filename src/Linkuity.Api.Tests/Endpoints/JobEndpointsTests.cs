using System.Net;
using System.Net.Http.Json;

namespace Linkuity.Api.Tests.Endpoints;

public class JobEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public JobEndpointsTests(TestWebApplicationFactory factory) => _factory = factory;

    private static object CreateRequestBody(string contentType) => new
    {
        configuration = new
        {
            contentType,
            fields = new[] { new { name = "email", semanticType = "email" } }
        }
    };

    [Fact]
    public async Task CreateJob_PersonContentType_Returns201Created()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/jobs", CreateRequestBody("person"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateJob_PeopleContentType_Returns400WithAcceptedValuesInBody()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/jobs", CreateRequestBody("people"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("person", body);
        Assert.Contains("organization", body);
    }

    [Fact]
    public async Task CreateJob_UnknownContentType_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/jobs", CreateRequestBody("spaceship"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
