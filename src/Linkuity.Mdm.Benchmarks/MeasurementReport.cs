using System.Globalization;
using System.Text;

namespace Linkuity.Mdm.Benchmarks;

/// <summary>A single per-batch measurement row produced by the ingest runner.</summary>
public record MeasurementRow(
    int BatchIndex,
    int RecordsInBatch,
    int CumulativeRecords,
    double ElapsedMs,
    double PeakWorkingSetMb);

/// <summary>Configuration for a measurement run.</summary>
public record MeasurementSetup(
    string Backend,
    double AutoMatchThreshold = 0.90,
    double ReviewThreshold = 0.75);

/// <summary>
/// Holds the per-batch rows produced by <see cref="IngestMeasurement.RunAsync"/> and renders
/// them as CSV or Markdown.
/// </summary>
public sealed class MeasurementReport
{
    /// <summary>Label identifying the backend under measurement (e.g. "File", "Postgres").</summary>
    public string Backend { get; }

    /// <summary>One row per ingested batch, in order.</summary>
    public IReadOnlyList<MeasurementRow> Rows { get; }

    public MeasurementReport(string backend, IReadOnlyList<MeasurementRow> rows)
    {
        Backend = backend;
        Rows = rows;
    }

    /// <summary>
    /// Renders results as a CSV string with LF line endings and InvariantCulture number formatting.
    /// Header: backend,batch_index,records_in_batch,cumulative_records,elapsed_ms,peak_working_set_mb
    /// </summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append("backend,batch_index,records_in_batch,cumulative_records,elapsed_ms,peak_working_set_mb\n");
        foreach (var row in Rows)
        {
            sb.Append(Backend).Append(',')
              .Append(row.BatchIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.RecordsInBatch.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.CumulativeRecords.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.ElapsedMs.ToString("G", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.PeakWorkingSetMb.ToString("G", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Renders results as a GitHub-flavored Markdown table.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("| backend | batch_index | records_in_batch | cumulative_records | elapsed_ms | peak_working_set_mb |\n");
        sb.Append("|---------|-------------|------------------|--------------------|------------|---------------------|\n");
        foreach (var row in Rows)
        {
            sb.Append("| ").Append(Backend)
              .Append(" | ").Append(row.BatchIndex.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(row.RecordsInBatch.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(row.CumulativeRecords.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(row.ElapsedMs.ToString("G", CultureInfo.InvariantCulture))
              .Append(" | ").Append(row.PeakWorkingSetMb.ToString("G", CultureInfo.InvariantCulture))
              .Append(" |\n");
        }
        return sb.ToString();
    }
}
