namespace Revelei.Imaging.Tests;

/// <summary>PB28: undo, redo, the forward branch dropped by a new step, and versions spilled past the budget.</summary>
public sealed class ImageHistoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pb-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void UndoAndRedoMoveThroughTheVersions()
    {
        using ImageHistory history = new(folder: folder);
        RgbaImage opened = Filled(4, 4, 1);
        RgbaImage upscaled = Filled(16, 16, 2);
        history.Reset(opened, "Opened a.jpg");
        history.Push(upscaled, "Upscale 4×");

        Assert.Same(opened, history.Undo());
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.Same(upscaled, history.Redo());
        Assert.False(history.CanRedo);
        Assert.Equal(["Opened a.jpg", "Upscale 4×"], history.Labels);
    }

    [Fact]
    public void ANewStepAfterUndoDropsTheForwardBranch()
    {
        using ImageHistory history = new(folder: folder);
        history.Reset(Filled(2, 2, 1), "Opened");
        history.Push(Filled(4, 4, 2), "Upscale 2×");
        history.Undo();

        history.Push(Filled(8, 8, 3), "Upscale 4×");

        Assert.Equal(["Opened", "Upscale 4×"], history.Labels);
        Assert.Equal(1, history.Position);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void ResetStartsAgainFromTheNewImage()
    {
        using ImageHistory history = new(folder: folder);
        history.Reset(Filled(2, 2, 1), "Opened a.jpg");
        history.Push(Filled(4, 4, 2), "Upscale 2×");

        history.Reset(Filled(3, 3, 9), "Opened b.png");

        Assert.Equal(["Opened b.png"], history.Labels);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void VersionsPastTheBudgetAreSpilledAndReadBackExactly()
    {
        // Each 16 x 16 version is 2 KiB of samples; a 3 KiB budget holds one besides the current.
        using ImageHistory history = new(budget: 3 * 1024, folder: folder);
        RgbaImage first = Filled(16, 16, 11);
        ushort[] firstSamples = first.Samples.ToArray();
        history.Reset(first, "Opened");
        history.Push(Filled(16, 16, 22), "Step 1");
        history.Push(Filled(16, 16, 33), "Step 2");

        Assert.Equal(2, history.Spilled);
        Assert.NotEmpty(Directory.GetFiles(folder));

        history.Undo();
        RgbaImage back = history.Undo()!;

        Assert.Equal(firstSamples, back.Samples.ToArray());
        Assert.Equal((16, 16), (back.Width, back.Height));
    }

    [Fact]
    public void DisposingDeletesTheSpilledFiles()
    {
        ImageHistory history = new(budget: 0, folder: folder);
        history.Reset(Filled(8, 8, 1), "Opened");
        history.Push(Filled(8, 8, 2), "Step");

        history.Dispose();

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void AnEmptyHistoryHasNothingToUndoOrRedo()
    {
        using ImageHistory history = new(folder: folder);

        Assert.Null(history.Current);
        Assert.Null(history.Undo());
        Assert.Null(history.Redo());
    }

    private static RgbaImage Filled(int w, int h, ushort value)
    {
        RgbaImage image = new(w, h, 16);
        image.Samples.Fill(value);
        return image;
    }
}
