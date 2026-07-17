using Linkuity.Core.Models;
using Linkuity.Core.Validation;

namespace Linkuity.Core.Tests.Validation;

public class MatchConfigurationValidatorTests
{
    private static MatchConfiguration ConfigWith(string contentType) => new()
    {
        ContentType = contentType,
        Fields = new[] { new Field { Name = "email", SemanticType = SemanticFieldType.Email } }
    };

    [Fact]
    public void Validate_PersonConfig_ReturnsOk()
    {
        var result = MatchConfigurationValidator.Validate(ConfigWith("person"));

        Assert.IsType<ValidationResult.Ok>(result);
    }

    [Fact]
    public void Validate_OrganizationConfig_ReturnsOk()
    {
        var result = MatchConfigurationValidator.Validate(ConfigWith("organization"));

        Assert.IsType<ValidationResult.Ok>(result);
    }

    [Fact]
    public void Validate_UnknownContentType_ReturnsInvalidWithProvidedAndAccepted()
    {
        var result = MatchConfigurationValidator.Validate(ConfigWith("spaceship"));

        var invalid = Assert.IsType<ValidationResult.InvalidContentType>(result);
        Assert.Equal("spaceship", invalid.Provided);
        Assert.Contains("person", invalid.Accepted);
        Assert.Contains("organization", invalid.Accepted);
    }

    [Fact]
    public void Validate_EmptyContentType_ReturnsInvalid()
    {
        var result = MatchConfigurationValidator.Validate(ConfigWith(""));

        Assert.IsType<ValidationResult.InvalidContentType>(result);
    }
}
