namespace Revelei.Inference.Tests;

/// <summary>
/// PB74: the models move to a chosen folder by copy, check and delete, and a move that cannot
/// finish leaves them where they were.
/// </summary>
public sealed class ModelsFolderMoveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-move-" + Guid.NewGuid().ToString("N"));

    private string From => Path.Combine(root, "old");

    private string To => Path.Combine(root, "new");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EveryFileMovesAndTheOldFolderGoes()
    {
        Write("tiny-x4/fp32.onnx", "weights");
        Write("tiny-x4/meta.json", "{}");
        Write(".shared/abc.part", "half");
        List<(long Done, long Total)> told = [];

        long moved = await ModelsFolderMove.MoveAsync(From, To, new Inline(told), TestContext.Current.CancellationToken);

        Assert.Equal(13, moved);
        Assert.Equal("weights", await File.ReadAllTextAsync(Path.Combine(To, "tiny-x4", "fp32.onnx"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(To, ".shared", "abc.part")));
        Assert.False(Directory.Exists(From));
        Assert.Equal((13, 13), told[^1]);
    }

    [Fact]
    public async Task ACancelledMoveRemovesTheCopyAndKeepsTheModels()
    {
        Write("a/fp32.onnx", "weights");
        Write("b/fp32.onnx", "weights");
        using CancellationTokenSource cancel = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelsFolderMove.MoveAsync(From, To, new Cancelling(cancel), cancel.Token));

        Assert.Equal(2, Directory.GetFiles(From, "*", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.Exists(To) ? Directory.GetFiles(To, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task AFolderInsideTheOldOneOrHoldingTheSameFilesOrTooSmallIsRefused()
    {
        Write("tiny-x4/fp32.onnx", "weights");

        await Assert.ThrowsAsync<ArgumentException>(() => ModelsFolderMove.MoveAsync(From, Path.Combine(From, "inner"), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ModelsFolderMove.MoveAsync(From, From + Path.DirectorySeparatorChar, null, TestContext.Current.CancellationToken));

        Directory.CreateDirectory(Path.Combine(To, "tiny-x4"));
        await File.WriteAllTextAsync(Path.Combine(To, "tiny-x4", "fp32.onnx"), "other", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => ModelsFolderMove.MoveAsync(From, To, null, TestContext.Current.CancellationToken));
        Assert.Equal("weights", await File.ReadAllTextAsync(Path.Combine(From, "tiny-x4", "fp32.onnx"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NothingToMoveIsNotAnError() =>
        Assert.Equal(0, await ModelsFolderMove.MoveAsync(From, To, null, TestContext.Current.CancellationToken));

    private void Write(string relative, string content)
    {
        string path = Path.Combine(From, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class Inline(List<(long, long)> told) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => told.Add(value);
    }

    // Cancels once the first file is copied, so the second never is.
    private sealed class Cancelling(CancellationTokenSource cancel) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => cancel.Cancel();
    }
}
