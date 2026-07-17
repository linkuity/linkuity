using Linkuity.Core.Vocabulary;

namespace Linkuity.Core.Tests.Vocabulary;

public class ContentTypeVocabularyTests
{
    [Fact]
    public void TryGetLabel_KnownPerson_ReturnsTrueAndPersonLabel()
    {
        var found = ContentTypeVocabulary.TryGetLabel("person", out var label);

        Assert.True(found);
        Assert.Equal("Person", label);
    }

    [Fact]
    public void TryGetLabel_KnownOrganization_ReturnsTrueAndOrganizationLabel()
    {
        var found = ContentTypeVocabulary.TryGetLabel("organization", out var label);

        Assert.True(found);
        Assert.Equal("Organization", label);
    }

    [Fact]
    public void TryGetLabel_UnknownContentType_ReturnsFalse()
    {
        var found = ContentTypeVocabulary.TryGetLabel("people", out var label);

        Assert.False(found);
        Assert.Null(label);
    }

    [Fact]
    public void TryGetLabel_CaseSensitive_DoesNotMatchCapitalized()
    {
        var found = ContentTypeVocabulary.TryGetLabel("Person", out _);

        Assert.False(found);
    }

    [Fact]
    public void AcceptedContentTypes_ContainsExactlyPersonAndOrganization()
    {
        var accepted = ContentTypeVocabulary.AcceptedContentTypes;

        Assert.Equal(2, accepted.Count);
        Assert.Contains("person", accepted);
        Assert.Contains("organization", accepted);
    }
}
