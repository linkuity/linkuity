using Linkuity.Core.Models;
using Linkuity.Core.Vocabulary;

namespace Linkuity.Core.Validation;

public abstract record ValidationResult
{
    public sealed record Ok : ValidationResult;
    public sealed record InvalidContentType(string Provided, IReadOnlyCollection<string> Accepted) : ValidationResult;
}

public static class MatchConfigurationValidator
{
    public static ValidationResult Validate(MatchConfiguration config)
    {
        if (!ContentTypeVocabulary.TryGetLabel(config.ContentType, out _))
            return new ValidationResult.InvalidContentType(
                config.ContentType,
                ContentTypeVocabulary.AcceptedContentTypes);
        return new ValidationResult.Ok();
    }
}
