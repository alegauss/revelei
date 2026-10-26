using System.Globalization;
using Revelei.Inference;

namespace Revelei.App.Tests;

/// <summary>PB58: a download reads as megabytes done of the total, the speed and the time left.</summary>
public sealed class DownloadTextTests
{
    public DownloadTextTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void SizesReadInMegabytesThenGigabytes()
    {
        Assert.Equal("325 MB", DownloadText.Size(340351416));
        Assert.Equal("1 MB", DownloadText.Size(1470));
        Assert.Equal("1.2 GB", DownloadText.Size(1288490189));
    }

    [Fact]
    public void ALineNamesWhatIsDoneTheSpeedAndTheTimeLeft()
    {
        DownloadProgress progress = new("gfpgan-v1.4/fp32.onnx", 120L << 20, 340351416, DownloadStage.Downloading, 8.4 * (1 << 20));

        Assert.Equal("120 of 325 MB · 8.4 MB/s · about 25 s left", DownloadText.Line(progress));
        Assert.Equal(36, DownloadText.Percent(progress));
    }

    [Fact]
    public void BeforeThereIsASpeedVerifyingAndRetryingSaySo()
    {
        Assert.Equal("0 of 325 MB · starting…", DownloadText.Line(new("f", 0, 340351416)));
        Assert.Equal("Verifying…", DownloadText.Line(new("f", 340351416, 340351416, DownloadStage.Verifying)));
        Assert.EndsWith("connection lost, trying again…", DownloadText.Line(new("f", 1, 340351416, DownloadStage.Retrying)), StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLeftIsRoundedTheWayItIsRead()
    {
        Assert.Equal("about 5 s left", DownloadText.Left(TimeSpan.FromSeconds(1)));
        Assert.Equal("about 25 s left", DownloadText.Left(TimeSpan.FromSeconds(23)));
        Assert.Equal("about 3 min left", DownloadText.Left(TimeSpan.FromSeconds(150)));
    }
}
