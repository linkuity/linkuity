using Linkuity.Mdm.Benchmarks;

namespace Linkuity.Mdm.Benchmarks.Tests;

public class MeasurementReportTests
{
    [Fact]
    public void ToCsv_EmitsHeaderAndOneRowPerBatch()
    {
        var report = new MeasurementReport("File", [
            new MeasurementRow(0, 50, 50, 12.0, 90.0),
            new MeasurementRow(1, 50, 100, 11.0, 95.0),
        ]);
        var csv = report.ToCsv();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("backend,batch_index,records_in_batch,cumulative_records,elapsed_ms,peak_working_set_mb", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("File,0,50,50,", lines[1]);
    }
}
