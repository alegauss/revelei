namespace Revelei.Inference.Tests;

/// <summary>PB41: the starting tile follows the memory there is, within 128 to 1024.</summary>
public sealed class TileSizerTests
{
    private const double X4 = 4096; // Real-ESRGAN x4plus's largest activation per input pixel (meta.json)

    [Fact]
    public void AnEightGigabyteGpuStartsWellAbove256ForX4()
    {
        // 6 GB free: (t + 64)^2 x 4096 x 6 <= 4.8 GB gives t = 384.
        Assert.Equal(384, TileSizer.Choose(6L << 30, X4, 32));
    }

    [Fact]
    public void ASmallGpuStartsSmaller()
    {
        int small = TileSizer.Choose(1L << 30, X4, 32);
        int large = TileSizer.Choose(6L << 30, X4, 32);

        Assert.True(small < large, $"{small} vs {large}");
        Assert.True(small >= TileSizer.MinTile);
    }

    [Fact]
    public void TheChoiceFitsWithItsOverlapAndTheNextStepDoesNot()
    {
        long free = 3L << 30;
        int tile = TileSizer.Choose(free, X4, 32);

        static double Need(int t) => Math.Pow(t + 64, 2) * X4 * TileSizer.ActivationMultiplier;

        Assert.True(Need(tile) <= free * TileSizer.Margin);
        Assert.True(Need(tile + TileSizer.Step) > free * TileSizer.Margin);
        Assert.Equal(0, tile % TileSizer.Step);
    }

    [Fact]
    public void PlentyOfMemoryCapsAt1024AndNoneFloorsAt128()
    {
        Assert.Equal(TileSizer.MaxTile, TileSizer.Choose(1L << 40, X4, 32));
        Assert.Equal(TileSizer.MinTile, TileSizer.Choose(0, X4, 32));
    }

    [Fact]
    public void TheCpuBudgetIsTheRamAvailable() =>
        Assert.True(MemoryBudget.Free(new ProviderSelection(ExecutionProvider.Cpu, null, null)) > 0);

    [Fact]
    public void EveryGpuOnThisDeskAnswersABudgetOrUnknownAndNeverThrows()
    {
        // PB56: VMware's adapter refuses the query (0x8876086A); 0 is "unknown", and the run then
        // starts from meta.json's recommended tile.
        foreach (GpuAdapter adapter in GpuAdapters.Enumerate())
        {
            Assert.True(MemoryBudget.Free(new ProviderSelection(ExecutionProvider.DirectML, adapter, null)) >= 0, adapter.Name);
        }
    }

    [Fact]
    public void AnAdapterDxgiDoesNotHaveIsUnknown() =>
        Assert.Equal(0, MemoryBudget.Free(new ProviderSelection(ExecutionProvider.DirectML, new GpuAdapter(99, "absent", 1L << 30), null)));
}
