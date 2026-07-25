using Linkuity.Core.Models;
using Linkuity.Matching.Blocking;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace Linkuity.Infrastructure.Lucene;

/// <summary>
/// Builds the boosted candidate query for an incoming record from its blocking keys:
/// exact blocking-key term clauses (high boost), phonetic term clauses (medium boost),
/// and fuzzy name clauses (low boost). Every clause is SHOULD; Lucene relevance only
/// orders candidates for Top-N selection and is never used as the match score.
/// Keys whose live block size (IndexReader.DocFreq — the inverted index is the
/// frequency store) exceeds maxBlockSize are suppressed entirely, including their
/// derived phonetic/fuzzy clauses: a block bigger than what Top-N can return was
/// already being truncated arbitrarily.
/// </summary>
internal static class CandidateQueryBuilder
{
    private const string PhoneticPrefix = "phonetic:";
    private const string NamePrefix = "name:";

    public static Query? Build(EntityRecord record, LuceneCandidateRetrievalOptions options, IndexReader reader, int maxBlockSize)
    {
        var policy = new BlockingKeySuppressionPolicy(maxBlockSize);
        var query = new BooleanQuery();
        var added = false;

        foreach (var key in record.BlockingKeys)
        {
            var term = new Term(LuceneFields.BlockingKey, key);
            if (policy.IsSuppressed(key, reader.DocFreq(term)))
                continue;

            query.Add(new TermQuery(term) { Boost = options.BlockingKeyBoost }, Occur.SHOULD);
            added = true;

            if (key.StartsWith(PhoneticPrefix, StringComparison.Ordinal))
            {
                var code = key[PhoneticPrefix.Length..];
                query.Add(new TermQuery(new Term(LuceneFields.Phonetic, code)) { Boost = options.PhoneticBoost }, Occur.SHOULD);
            }
            else if (key.StartsWith(NamePrefix, StringComparison.Ordinal) && options.FuzzyMaxEdits > 0)
            {
                var token = key[NamePrefix.Length..];
                query.Add(new FuzzyQuery(new Term(LuceneFields.Name, token), options.FuzzyMaxEdits) { Boost = options.FuzzyBoost }, Occur.SHOULD);
            }
        }

        return added ? query : null;
    }
}
