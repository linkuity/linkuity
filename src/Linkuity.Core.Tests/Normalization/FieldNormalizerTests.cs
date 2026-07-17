using Linkuity.Core.Models;
using Linkuity.Core.Normalization;

namespace Linkuity.Core.Tests.Normalization;

public class FieldNormalizerTests
{
    [Theory]
    [InlineData("USER@EXAMPLE.COM", "user@example.com")]
    [InlineData("  user@example.com  ", "user@example.com")]
    [InlineData("user@example.com", "user@example.com")]
    public void Normalize_Email_TrimsAndLowercases(string input, string expected)
        => Assert.Equal(expected, FieldNormalizer.Normalize(input, SemanticFieldType.Email));

    [Theory]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("  example.com  ", "example.com")]
    public void Normalize_DomainName_TrimsAndLowercases(string input, string expected)
        => Assert.Equal(expected, FieldNormalizer.Normalize(input, SemanticFieldType.DomainName));

    [Fact]
    public void Normalize_Phone_ValidUSWithCountryCode_ReturnsE164()
        => Assert.Equal("+18005550100", FieldNormalizer.Normalize("+1 800 555 0100", SemanticFieldType.Phone));

    [Fact]
    public void Normalize_Phone_ValidUSWithoutCountryCode_ReturnsE164()
        => Assert.Equal("+18005550100", FieldNormalizer.Normalize("(800) 555-0100", SemanticFieldType.Phone));

    [Fact]
    public void Normalize_Phone_ValidInternational_ReturnsE164()
        => Assert.Equal("+447400123456", FieldNormalizer.Normalize("+44 7400 123456", SemanticFieldType.Phone));

    [Fact]
    public void Normalize_Phone_Unparseable_ReturnsOriginal()
        => Assert.Equal("not-a-phone", FieldNormalizer.Normalize("not-a-phone", SemanticFieldType.Phone));

    [Theory]
    [InlineData("2025-01-15", "2025-01-15")]       // yyyy-MM-dd — already ISO
    [InlineData("2025/01/15", "2025-01-15")]       // yyyy/MM/dd
    [InlineData("01/15/2025", "2025-01-15")]       // MM/dd/yyyy
    [InlineData("1/15/2025", "2025-01-15")]        // M/d/yyyy
    [InlineData("Jan 15 2025", "2025-01-15")]      // MMM d yyyy
    [InlineData("January 15 2025", "2025-01-15")] // MMMM d yyyy
    [InlineData("15-01-2025", "15-01-2025")]       // unrecognized — passthrough
    public void Normalize_DateOfBirth_ConvertsToIsoOrPassesThrough(string input, string expected)
        => Assert.Equal(expected, FieldNormalizer.Normalize(input, SemanticFieldType.DateOfBirth));

    [Theory]
    [InlineData("Mr. John Doe", "John Doe")]
    [InlineData("Mrs. Jane Doe", "Jane Doe")]
    [InlineData("Ms. Jane Doe", "Jane Doe")]
    [InlineData("Miss Jane Doe", "Jane Doe")]
    [InlineData("Dr. House", "House")]
    [InlineData("Prof. Smith", "Smith")]
    [InlineData("DR. House", "House")]
    public void Normalize_FirstName_StripsHonorific(string input, string expected)
        => Assert.Equal(expected, FieldNormalizer.Normalize(input, SemanticFieldType.FirstName));

    [Fact]
    public void Normalize_LastName_NoHonorific_Unchanged()
        => Assert.Equal("Smith", FieldNormalizer.Normalize("Smith", SemanticFieldType.LastName));

    [Fact]
    public void Normalize_FullName_StripsHonorific()
        => Assert.Equal("John Doe", FieldNormalizer.Normalize("Prof. John Doe", SemanticFieldType.FullName));

    [Theory]
    [InlineData("  123 Main St  ", "123 Main St", SemanticFieldType.AddressLine)]
    [InlineData("  90210  ", "90210", SemanticFieldType.PostalCode)]
    [InlineData("  Acme Corp  ", "Acme Corp", SemanticFieldType.OrganizationName)]
    public void Normalize_TrimOnlyTypes_Trims(string input, string expected, SemanticFieldType type)
        => Assert.Equal(expected, FieldNormalizer.Normalize(input, type));

    [Fact]
    public void Normalize_EmptyString_ReturnsEmpty()
        => Assert.Equal(string.Empty, FieldNormalizer.Normalize(string.Empty, SemanticFieldType.Email));

    [Fact]
    public void Normalize_WhitespaceOnly_ReturnsEmpty()
        => Assert.Equal(string.Empty, FieldNormalizer.Normalize("   ", SemanticFieldType.Email));

    [Fact]
    public void Normalize_NewProductIdentifierTypes_PassThroughUnchanged()
    {
        Assert.Equal("ALPHA-100", FieldNormalizer.Normalize("ALPHA-100", SemanticFieldType.Sku));
        Assert.Equal("00012345600012", FieldNormalizer.Normalize("00012345600012", SemanticFieldType.Gtin));
        Assert.Equal("Widget Alpha", FieldNormalizer.Normalize("Widget Alpha", SemanticFieldType.ProductName));
    }
}
