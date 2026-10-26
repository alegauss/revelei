namespace Revelei.Inference.Tests;

/// <summary>PB22: one JSON line per operation, rotated so the folder stays bounded.</summary>
public sealed class OperationLogTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pb-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ARecordRoundTripsThroughTheFile()
    {
        OperationLog log = new(folder);
        OperationRecord record = Sample("ok") with { Retries = ["Out of memory on a 256 px tile; retrying at 128 px."] };

        log.Append(record);

        OperationRecord read = Assert.Single(log.ReadCurrent());
        Assert.Equal(record with { Retries = [] }, read with { Retries = [] });
        Assert.Equal(record.Retries, read.Retries);
        Assert.Contains("\"tiles\":", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Fact]
    public void AFullFileRotatesAndOnlyKeepFilesSurvive()
    {
        OperationLog log = new(folder, maxBytes: 600, keep: 2);

        for (int i = 0; i < 12; i++)
        {
            log.Append(Sample($"run {i}"));
        }

        string[] files = [.. Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
        Assert.Equal(["operations.1.jsonl", "operations.2.jsonl", "operations.jsonl"], files);
        Assert.All(Directory.GetFiles(folder), f => Assert.True(new FileInfo(f).Length <= 600));
        Assert.Equal("run 11", log.ReadCurrent()[^1].Outcome);
    }

    [Fact]
    public void NothingLoggedReadsEmpty() => Assert.Empty(new OperationLog(folder).ReadCurrent());

    [Fact]
    public void TheAppLogsUnderLocalAppData() =>
        Assert.EndsWith(Path.Combine("Revelei", "logs"), OperationLog.DefaultFolder, StringComparison.Ordinal);

    private static OperationRecord Sample(string outcome) => new(
        new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.FromHours(-3)),
        "upscale x4",
        "realesrgan-x4plus",
        "fp32",
        "DirectML",
        "NVIDIA GeForce RTX 4060",
        179,
        179,
        256,
        1,
        [],
        352.5,
        outcome);
}
