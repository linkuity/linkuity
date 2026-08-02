using System.Globalization;

namespace Linkuity.Infrastructure.Lucene;

/// <summary>
/// Blocking-key terms are stored scoped to their project, so that Lucene's own document
/// frequency for a term IS that key's block size within the project.
///
/// The alternative was counting <c>(project AND term)</c> per key, which turns an O(1) postings
/// lookup into a real search — several per incoming record, on the hot path. Folding the project
/// into the term gets the same answer for the same cost as before.
///
/// It corrects a real defect rather than only saving work. <c>IndexReader.DocFreq</c> counts
/// across the entire index, so before this a neighbouring project's records inflated a key's
/// apparent block size. A busy tenant could push another tenant's keys past MaxBlockSize and
/// suppress them outright, leaving records that retrieved nothing while their own project held
/// only a handful of documents. Retrieval is project-scoped, so the number that ought to govern
/// suppression was always the per-project one.
/// </summary>
internal static class ScopedBlockingKey
{
    /// <summary>
    /// Separates the 32-character project id from the key. Its position is fixed, so a term can
    /// be recognised as scoped without any assumption about what a key may contain.
    /// </summary>
    private const char Separator = '|';

    private const int ProjectIdLength = 32;   // Guid "N" format

    public static string For(Guid projectId, string key)
        => string.Concat(projectId.ToString("N", CultureInfo.InvariantCulture), Separator.ToString(), key);

    /// <summary>
    /// Whether a term read from an index carries a project scope. Used to reject indexes written
    /// before this change: their keys are unscoped, so no query would ever match them and
    /// retrieval would return nothing while appearing healthy.
    /// </summary>
    public static bool IsScoped(string term)
        => term.Length > ProjectIdLength && term[ProjectIdLength] == Separator;
}
