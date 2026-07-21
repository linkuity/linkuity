using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Core.Vocabulary;

namespace Linkuity.Pipeline;

public class Neo4jExportService
{
    private static readonly string[] KnownFileOrder =
    {
        "entities", "golden-records", "matched-to", "resolved-to",
        "sources", "from-source", "emails", "has-email", "phones", "has-phone"
    };

    private static readonly HashSet<string> KnownFiles = new(KnownFileOrder, StringComparer.Ordinal);

    private static string BuildLoadCypherScript(string? typeLabel)
    {
        var entitySuffix = typeLabel is null ? "" : $":{typeLabel}";
        return $$"""
            // 1) Constraints (run once per database)
            CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE;
            CREATE CONSTRAINT cluster_id IF NOT EXISTS FOR (g:GoldenRecord) REQUIRE g.cluster_id IS UNIQUE;
            CREATE CONSTRAINT email_value IF NOT EXISTS FOR (m:Email) REQUIRE m.value IS UNIQUE;
            CREATE CONSTRAINT phone_value IF NOT EXISTS FOR (p:Phone) REQUIRE p.value IS UNIQUE;
            CREATE CONSTRAINT source_name IF NOT EXISTS FOR (s:Source) REQUIRE s.name IS UNIQUE;

            // 2) Nodes
            LOAD CSV WITH HEADERS FROM 'file:///entities.csv' AS row
            MERGE (e:Entity{{entitySuffix}} {id: row.id}) SET e += row;

            LOAD CSV WITH HEADERS FROM 'file:///golden-records.csv' AS row
            MERGE (g:GoldenRecord{{entitySuffix}} {cluster_id: row.cluster_id}) SET g += row;

            LOAD CSV WITH HEADERS FROM 'file:///emails.csv' AS row
            MERGE (:Email {value: row.value});

            LOAD CSV WITH HEADERS FROM 'file:///phones.csv' AS row
            MERGE (:Phone {value: row.value});

            LOAD CSV WITH HEADERS FROM 'file:///sources.csv' AS row
            MERGE (:Source {name: row.name});

            // 3) Relationships
            LOAD CSV WITH HEADERS FROM 'file:///matched-to.csv' AS row
            MATCH (l:Entity {id: row.left_id})
            MATCH (r:Entity {id: row.right_id})
            MERGE (l)-[:MATCHED_TO {
              similarity: toFloat(row.similarity),
              fuzzy_similarity: CASE row.fuzzy_similarity WHEN '' THEN null ELSE toFloat(row.fuzzy_similarity) END
            }]-(r);

            LOAD CSV WITH HEADERS FROM 'file:///resolved-to.csv' AS row
            MATCH (e:Entity {id: row.entity_id})
            MATCH (g:GoldenRecord {cluster_id: row.cluster_id})
            MERGE (e)-[:RESOLVED_TO {cluster_size: toInteger(row.cluster_size)}]->(g);

            LOAD CSV WITH HEADERS FROM 'file:///from-source.csv' AS row
            MATCH (e:Entity {id: row.entity_id})
            MATCH (s:Source {name: row.source_name})
            MERGE (e)-[:FROM_SOURCE]->(s);

            LOAD CSV WITH HEADERS FROM 'file:///has-email.csv' AS row
            MATCH (e:Entity {id: row.entity_id})
            MATCH (m:Email {value: row.email_value})
            MERGE (e)-[:HAS_EMAIL]->(m);

            LOAD CSV WITH HEADERS FROM 'file:///has-phone.csv' AS row
            MATCH (e:Entity {id: row.entity_id})
            MATCH (p:Phone {value: row.phone_value})
            MERGE (e)-[:HAS_PHONE]->(p);
            """;
    }

    private readonly IBlobStore _blobs;

    public Neo4jExportService(IBlobStore blobs) => _blobs = blobs;

    public async Task<Neo4jFileResult> OpenAsync(Guid jobId, string fileKey, CancellationToken ct = default)
    {
        if (!KnownFiles.Contains(fileKey)) return new Neo4jFileResult.UnknownFile();
        var job = await _blobs.ReadJsonAsync<Job>($"{jobId}/metadata.json", ct);
        if (job is null) return new Neo4jFileResult.JobNotFound();
        if (job.State != JobState.Complete) return new Neo4jFileResult.NotReady(job.State);

        return new Neo4jFileResult.Ready(await GenerateFileAsync(jobId, fileKey, job, ct));
    }

    public async Task<Neo4jExportResult> OpenZipAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _blobs.ReadJsonAsync<Job>($"{jobId}/metadata.json", ct);
        if (job is null) return new Neo4jExportResult.JobNotFound();
        if (job.State != JobState.Complete) return new Neo4jExportResult.NotReady(job.State);

        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fileKey in KnownFileOrder)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry($"{fileKey}.csv", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var content = await GenerateFileAsync(jobId, fileKey, job, ct);
                await content.CopyToAsync(entryStream, ct);
            }

            var cypherEntry = archive.CreateEntry("load.cypher", CompressionLevel.Optimal);
            await using var cypherStream = cypherEntry.Open();
            var typeLabel = ContentTypeVocabulary.TryGetLabel(job.Configuration.ContentType, out var resolved) ? resolved : null;
            var cypherBytes = Encoding.UTF8.GetBytes(BuildLoadCypherScript(typeLabel));
            await cypherStream.WriteAsync(cypherBytes, ct);
        }

        ms.Position = 0;
        return new Neo4jExportResult.Ready(ms);
    }

    private async Task<Stream> GenerateFileAsync(Guid jobId, string fileKey, Job job, CancellationToken ct) => fileKey switch
    {
        "entities" => await _blobs.DownloadAsync($"{jobId}/normalized.csv", ct),
        "golden-records" =>
            await DropColumnAsync(await _blobs.DownloadAsync($"{jobId}/golden_records.csv", ct), "member_ids", ct),
        "matched-to" => await _blobs.DownloadAsync($"{jobId}/matches.csv", ct),
        "resolved-to" => await GenerateResolvedToAsync(jobId, ct),
        "emails" => await GenerateDistinctValuesAsync(jobId, FieldByType(job, SemanticFieldType.Email), "value", ct),
        "has-email" => await GenerateEntityToValueAsync(jobId, FieldByType(job, SemanticFieldType.Email), "entity_id", "email_value", ct),
        "phones" => await GenerateDistinctValuesAsync(jobId, FieldByType(job, SemanticFieldType.Phone), "value", ct),
        "has-phone" => await GenerateEntityToValueAsync(jobId, FieldByType(job, SemanticFieldType.Phone), "entity_id", "phone_value", ct),
        "sources" => await GenerateDistinctValuesAsync(jobId, FieldByType(job, SemanticFieldType.SourceIdentifier), "name", ct),
        "from-source" => await GenerateEntityToValueAsync(jobId, FieldByType(job, SemanticFieldType.SourceIdentifier), "entity_id", "source_name", ct),
        _ => throw new InvalidOperationException($"Unhandled known file '{fileKey}' — this is a bug")
    };

    private static string? FieldByType(Job job, SemanticFieldType type)
        => job.Configuration.Fields.FirstOrDefault(f => f.SemanticType == type)?.Name;

    private async Task<Stream> GenerateDistinctValuesAsync(Guid jobId, string? fieldName, string outputHeader, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        await using var csvOut = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);

        csvOut.WriteField(outputHeader);
        await csvOut.NextRecordAsync();

        if (fieldName is not null)
        {
            await using var src = await _blobs.DownloadAsync($"{jobId}/normalized.csv", ct);
            using var reader = new StreamReader(src);
            using var csvIn = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
            await csvIn.ReadAsync();
            csvIn.ReadHeader();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await csvIn.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                var v = csvIn.GetField(fieldName) ?? "";
                if (!string.IsNullOrEmpty(v) && seen.Add(v))
                {
                    csvOut.WriteField(v);
                    await csvOut.NextRecordAsync();
                }
            }
        }

        await writer.FlushAsync(ct);
        ms.Position = 0;
        return ms;
    }

    private async Task<Stream> GenerateEntityToValueAsync(Guid jobId, string? fieldName, string entityHeader, string valueHeader, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        await using var csvOut = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);

        csvOut.WriteField(entityHeader);
        csvOut.WriteField(valueHeader);
        await csvOut.NextRecordAsync();

        if (fieldName is not null)
        {
            await using var src = await _blobs.DownloadAsync($"{jobId}/normalized.csv", ct);
            using var reader = new StreamReader(src);
            using var csvIn = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
            await csvIn.ReadAsync();
            csvIn.ReadHeader();
            while (await csvIn.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                var id = csvIn.GetField("id") ?? "";
                var v = csvIn.GetField(fieldName) ?? "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(v))
                {
                    csvOut.WriteField(id);
                    csvOut.WriteField(v);
                    await csvOut.NextRecordAsync();
                }
            }
        }

        await writer.FlushAsync(ct);
        ms.Position = 0;
        return ms;
    }

    private async Task<Stream> GenerateResolvedToAsync(Guid jobId, CancellationToken ct)
    {
        await using var src = await _blobs.DownloadAsync($"{jobId}/golden_records.csv", ct);
        using var reader = new StreamReader(src);
        using var csvIn = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        await using var csvOut = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);

        csvOut.WriteField("entity_id");
        csvOut.WriteField("cluster_id");
        csvOut.WriteField("cluster_size");
        await csvOut.NextRecordAsync();

        await csvIn.ReadAsync();
        csvIn.ReadHeader();

        while (await csvIn.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var clusterId = csvIn.GetField("cluster_id") ?? "";
            var recordCount = csvIn.GetField("record_count") ?? "";
            var memberIds = (csvIn.GetField("member_ids") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var memberId in memberIds)
            {
                csvOut.WriteField(memberId);
                csvOut.WriteField(clusterId);
                csvOut.WriteField(recordCount);
                await csvOut.NextRecordAsync();
            }
        }

        await writer.FlushAsync(ct);
        ms.Position = 0;
        return ms;
    }

    private static async Task<Stream> DropColumnAsync(Stream source, string columnToDrop, CancellationToken ct)
    {
        using var reader = new StreamReader(source);
        using var csvIn = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        await using var csvOut = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);

        await csvIn.ReadAsync();
        csvIn.ReadHeader();
        var headers = csvIn.HeaderRecord!.Where(h => h != columnToDrop).ToArray();
        foreach (var h in headers) csvOut.WriteField(h);
        await csvOut.NextRecordAsync();

        while (await csvIn.ReadAsync())
        {
            foreach (var h in headers) csvOut.WriteField(csvIn.GetField(h) ?? "");
            await csvOut.NextRecordAsync();
        }

        await writer.FlushAsync(ct);
        ms.Position = 0;
        return ms;
    }
}

public abstract record Neo4jFileResult
{
    public sealed record JobNotFound : Neo4jFileResult;
    public sealed record NotReady(JobState State) : Neo4jFileResult;
    public sealed record UnknownFile : Neo4jFileResult;
    public sealed record Ready(Stream Content) : Neo4jFileResult;
}

public abstract record Neo4jExportResult
{
    public sealed record JobNotFound : Neo4jExportResult;
    public sealed record NotReady(JobState State) : Neo4jExportResult;
    public sealed record Ready(Stream Content) : Neo4jExportResult;
}
