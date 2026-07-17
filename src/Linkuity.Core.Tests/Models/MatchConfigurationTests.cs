using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Core.Models;

namespace Linkuity.Core.Tests.Models;

public class MatchConfigurationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    [Fact]
    public void Deserializes_FromValidJson()
    {
        var json = """
            {
              "contentType": "person",
              "fields": [
                { "name": "first_name", "semanticType": "first_name" },
                { "name": "dob",        "semanticType": "date_of_birth" }
              ]
            }
            """;

        var config = JsonSerializer.Deserialize<MatchConfiguration>(json, Options);

        Assert.NotNull(config);
        Assert.Equal("person", config.ContentType);
        Assert.Equal(2, config.Fields.Count);
        Assert.Equal("first_name", config.Fields[0].Name);
        Assert.Equal(SemanticFieldType.FirstName, config.Fields[0].SemanticType);
        Assert.Equal("dob", config.Fields[1].Name);
        Assert.Equal(SemanticFieldType.DateOfBirth, config.Fields[1].SemanticType);
    }

    [Fact]
    public void Serializes_SemanticType_AsSnakeCaseString()
    {
        var config = new MatchConfiguration
        {
            ContentType = "organization",
            Fields =
            [
                new Field { Name = "org", SemanticType = SemanticFieldType.OrganizationName }
            ]
        };

        var json = JsonSerializer.Serialize(config, Options);

        Assert.Contains("\"organization_name\"", json);
        Assert.Contains("\"org\"", json);
        Assert.Contains("\"organization\"", json);
    }

    [Fact]
    public void Deserializes_ParticipatesInMatching_DefaultsToTrueWhenOmitted()
    {
        var json = """
            {
              "contentType": "person",
              "fields": [
                { "name": "email",  "semanticType": "email" },
                { "name": "phone",  "semanticType": "phone", "participatesInMatching": false }
              ]
            }
            """;

        var config = JsonSerializer.Deserialize<MatchConfiguration>(json, Options);

        Assert.NotNull(config);
        Assert.True(config.Fields[0].ParticipatesInMatching);   // omitted → default true
        Assert.False(config.Fields[1].ParticipatesInMatching);  // explicit false
    }
}
