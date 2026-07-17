using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Core.Models;

namespace Linkuity.Core.Tests.Models;

public class SemanticFieldTypeTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    [Theory]
    [InlineData(SemanticFieldType.FirstName,       "\"first_name\"")]
    [InlineData(SemanticFieldType.LastName,        "\"last_name\"")]
    [InlineData(SemanticFieldType.FullName,        "\"full_name\"")]
    [InlineData(SemanticFieldType.Email,           "\"email\"")]
    [InlineData(SemanticFieldType.Phone,           "\"phone\"")]
    [InlineData(SemanticFieldType.DateOfBirth,     "\"date_of_birth\"")]
    [InlineData(SemanticFieldType.AddressLine,     "\"address_line\"")]
    [InlineData(SemanticFieldType.PostalCode,      "\"postal_code\"")]
    [InlineData(SemanticFieldType.OrganizationName,"\"organization_name\"")]
    [InlineData(SemanticFieldType.DomainName,      "\"domain_name\"")]
    public void Serializes_ToSnakeCaseJsonString(SemanticFieldType type, string expected)
    {
        var json = JsonSerializer.Serialize(type, Options);
        Assert.Equal(expected, json);
    }

    [Theory]
    [InlineData("\"first_name\"",        SemanticFieldType.FirstName)]
    [InlineData("\"date_of_birth\"",     SemanticFieldType.DateOfBirth)]
    [InlineData("\"organization_name\"", SemanticFieldType.OrganizationName)]
    [InlineData("\"email\"",             SemanticFieldType.Email)]
    [InlineData("\"phone\"",             SemanticFieldType.Phone)]
    public void Deserializes_FromSnakeCaseJsonString(string json, SemanticFieldType expected)
    {
        var result = JsonSerializer.Deserialize<SemanticFieldType>(json, Options);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Deserializes_UnknownValue_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SemanticFieldType>("\"zip_code\"", Options));
    }

    [Fact]
    public void Deserializes_IntegerValue_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SemanticFieldType>("0", Options));
    }
}
