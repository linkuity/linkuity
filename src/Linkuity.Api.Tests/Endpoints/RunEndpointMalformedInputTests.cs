using System.Net;
using System.Net.Http.Headers;

namespace Linkuity.Api.Tests.Endpoints;

public sealed class RunEndpointMalformedInputTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public RunEndpointMalformedInputTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PostRun_MalformedCsv_Returns400NotServerError()
    {
        var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(
            new StringContent("{\"configuration\":{\"contentType\":\"person\",\"fields\":[" +
                "{\"name\":\"source\",\"semanticType\":\"source_identifier\"}," +
                "{\"name\":\"first_name\",\"semanticType\":\"first_name\"}]}}"),
            "config");

        // Unterminated quoted field: CsvHelper's default RFC4180 mode hits end-of-file
        // while still inside a quoted field and throws a CsvHelperException.
        const string malformedCsv = "source,first_name\nCRM,\"Unterminated field with no closing quote\n";
        var csv = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(malformedCsv));
        csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csv, "file", "malformed.csv");

        var response = await client.PostAsync("/run", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Could not process input", await response.Content.ReadAsStringAsync());
    }
}
