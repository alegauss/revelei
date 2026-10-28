using Revelei.Generation;

namespace Revelei.App.Tests;

/// <summary>PB80: the Create screen's status line says the stage, the step, the time taken and the time left.</summary>
public sealed class CreateTextTests
{
    [Fact]
    public void LoadingAndDecodingSayTheStageAndTheTimeTaken()
    {
        Assert.Equal("Loading the model · 3 s", CreateText.Line(new(GenerationStage.Loading, 10, 400), TimeSpan.FromSeconds(3), null));
        Assert.Equal("Turning it into pixels · 1 min 12 s", CreateText.Line(new(GenerationStage.Decoding, 1, 9), TimeSpan.FromSeconds(72), TimeSpan.FromSeconds(50)));
    }

    [Fact]
    public void ASamplingStepSaysTheTimeLeftOnlyOnceTheFirstStepIsBehindIt()
    {
        // The first step also moves the weights to the GPU, so the pace is taken after it.
        Assert.Equal("Step 1 of 8 · 14 s", CreateText.Line(new(GenerationStage.Sampling, 1, 8), TimeSpan.FromSeconds(14), TimeSpan.Zero));
        Assert.Equal("Step 3 of 8 · 18 s · about 10 s left", CreateText.Line(new(GenerationStage.Sampling, 3, 8), TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(4)));
    }
}
