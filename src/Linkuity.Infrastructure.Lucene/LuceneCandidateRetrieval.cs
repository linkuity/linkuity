using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Linkuity.Infrastructure.Lucene;

/// <summary>
/// Lucene.NET candidate retrieval: a durable inverted index over EntityRecords that
/// returns Top-N candidates for an incoming record, replacing the linear scan. The
/// index is a derived artifact — it can be rebuilt from durable records at any time.
/// Lucene relevance is used only to select and order candidates; the engine assigns
/// the actual match score downstream.
/// </summary>
public sealed class LuceneCandidateRetrieval : IIndexedCandidateRetrievalStrategy
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    private readonly LuceneCandidateRetrievalOptions _options;
    private readonly FSDirectory _directory;
    private readonly IndexWriter _writer;
    private bool _disposed;

    // Per-thread near-real-time retrieval readers. Each thread opens its OWN DirectoryReader over
    // the committed index, so concurrent stored-field reads (searcher.Doc) hit independent
    // IndexInputs and do not serialize on a shared reader (Milestone 26). A reader is reused across
    // a thread's Retrieve calls and reopened only when the index generation changes (bumped by
    // Commit). The Postgres ingest path commits after every batch and matches batch-mates in memory,
    // so a committed reader always sees exactly the corpus edge production needs.
    //
    // Index mutations (Index/Update/Remove/Rebuild/Commit) remain sequential and MUST NOT run
    // concurrently with Retrieve — during ingest they execute in the batch write phase, after the
    // parallel edge-production phase has completed.
    //
    // Invariant for future callers: retrieval now observes only COMMITTED documents, so any caller
    // that indexes and then retrieves without an intervening Commit() will not see those writes.
    private readonly ThreadLocal<ReaderHandle?> _threadReader = new(trackAllValues: true);
    private long _generation;   // bumped on Commit; Volatile/Interlocked access
    private long _reopenCount;

    private sealed class ReaderHandle : IDisposable
    {
        public required DirectoryReader Reader { get; init; }
        public required IndexSearcher Searcher { get; init; }
        public required long Generation { get; init; }
        public void Dispose() => Reader.Dispose();
    }

    /// <summary>
    /// Diagnostic counter incremented every time a <see cref="DirectoryReader"/> is (re)opened by
    /// any thread. Exposed for deterministic tests that assert readers are not reopened per
    /// <see cref="Retrieve"/> call and that concurrent retrieval uses bounded per-thread readers.
    /// </summary>
    public long ReopenCount => Volatile.Read(ref _reopenCount);

    public LuceneCandidateRetrieval(LuceneCandidateRetrievalOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.IndexDirectory))
            throw new ArgumentException("IndexDirectory must be set.", nameof(options));

        // Fail fast with a clear message when the index directory cannot be created / is not
        // writable, instead of surfacing an opaque FSDirectory/IndexWriter crash later.
        try
        {
            System.IO.Directory.CreateDirectory(options.IndexDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Lucene index directory '{options.IndexDirectory}' could not be created; it must be a writable path.", ex);
        }

        _directory = FSDirectory.Open(new DirectoryInfo(options.IndexDirectory));
        _writer = new IndexWriter(_directory, new IndexWriterConfig(Version, new StandardAnalyzer(Version)));

        // Ensure a commit point exists so committed-reader retrieval succeeds on an empty index
        // (the first batch's edge production runs before any records are indexed).
        _writer.Commit();
    }

    public string Name => "lucene";

    public long Count => _writer.NumDocs;

    public void Index(EntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _writer.AddDocument(EntityRecordDocumentMapper.ToDocument(record));
    }

    public void Commit()
    {
        _writer.Commit();
        // New committed data is now visible; force each thread to reopen its reader on next Retrieve.
        Interlocked.Increment(ref _generation);
    }

    public IReadOnlyList<EntityRecord> Retrieve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(record);

        var query = CandidateQueryBuilder.Build(record, _options);
        if (query is null)
            return [];

        var searcher = AcquireSearcher();
        var hits = searcher.Search(query, _options.MaxCandidates);

        var results = new List<EntityRecord>(hits.ScoreDocs.Length);
        foreach (var hit in hits.ScoreDocs)
        {
            // Read only the stored fields the scoring projection needs; skipping the rest avoids
            // decompressing/materializing unused stored data on the hot path (Milestone 26).
            var visitor = new DocumentStoredFieldVisitor(
                LuceneFields.Id, LuceneFields.ProjectId, LuceneFields.SourceRecordId, LuceneFields.FieldsJson);
            searcher.Doc(hit.Doc, visitor);
            results.Add(EntityRecordDocumentMapper.FromDocument(visitor.Document));
        }
        return results;
    }

    /// <summary>
    /// Returns this thread's cached committed searcher, opening a fresh <see cref="DirectoryReader"/>
    /// on first use and reopening it only when the index generation changed (a <see cref="Commit"/>
    /// happened) since this thread last opened. Each thread holds its own reader, so concurrent
    /// Search/Doc calls do not share a stored-field <c>IndexInput</c>.
    /// </summary>
    private IndexSearcher AcquireSearcher()
    {
        var generation = Volatile.Read(ref _generation);
        var handle = _threadReader.Value;
        if (handle is null || handle.Generation != generation)
        {
            handle?.Dispose();
            var reader = DirectoryReader.Open(_directory);
            handle = new ReaderHandle { Reader = reader, Searcher = new IndexSearcher(reader), Generation = generation };
            _threadReader.Value = handle;
            Interlocked.Increment(ref _reopenCount);
        }
        return handle.Searcher;
    }

    public void Update(EntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _writer.UpdateDocument(new Term(LuceneFields.Id, record.Id.ToString()), EntityRecordDocumentMapper.ToDocument(record));
    }

    public void Remove(Guid recordId)
    {
        _writer.DeleteDocuments(new Term(LuceneFields.Id, recordId.ToString()));
    }

    public void Rebuild(IEnumerable<EntityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _writer.DeleteAll();
        foreach (var record in records)
            _writer.AddDocument(EntityRecordDocumentMapper.ToDocument(record));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var handle in _threadReader.Values)
            handle?.Dispose();
        _threadReader.Dispose();
        _writer.Commit();
        _writer.Dispose();
        _directory.Dispose();
    }
}
