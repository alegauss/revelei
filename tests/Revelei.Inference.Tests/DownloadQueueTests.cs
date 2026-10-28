namespace Revelei.Inference.Tests;

/// <summary>
/// PB73: models download one at a time in queue order, each can be paused, resumed, moved up or
/// cancelled, and the queue is read back from disk after a restart.
/// </summary>
public sealed class DownloadQueueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-queue-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(root, "queue.json");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueuedModelsDownloadOneAtATimeInOrderAfterARestart()
    {
        DownloadQueue before = new(File);
        foreach (string model in (string[])["a", "b", "c", "d"])
        {
            before.Enqueue(model, "fp32");
        }

        before.Pause("b");
        before.MoveUp("c");
        before.MoveUp("c");
        before.MoveUp("c");
        before.Cancel("d");

        // A restart: the queue is read back as it was left.
        DownloadQueue after = new(File);
        Assert.Equal([new("c", "fp32"), new("a", "fp32"), new("b", "fp32", Paused: true)], after.Items);

        List<string> fetched = [];
        int atOnce = 0, most = 0;
        await after.RunAsync(
            async (item, token) =>
            {
                most = Math.Max(most, Interlocked.Increment(ref atOnce));
                await Task.Yield();
                fetched.Add(item.Model);
                Interlocked.Decrement(ref atOnce);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["c", "a"], fetched);
        Assert.Equal(1, most);
        Assert.Equal([new("b", "fp32", Paused: true)], new DownloadQueue(File).Items);
    }

    [Fact]
    public async Task PausingTheRunningModelStopsItAndTheNextOneGoes()
    {
        DownloadQueue queue = new(File);
        queue.Enqueue("big", "q4");
        queue.Enqueue("small", "fp32");
        List<string> finished = [];

        await queue.RunAsync(
            async (item, token) =>
            {
                if (item.Model == "big")
                {
                    queue.Pause("big");
                    await Task.Delay(Timeout.Infinite, token);
                }

                finished.Add(item.Model);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["small"], finished);
        Assert.Equal([new("big", "q4", Paused: true)], queue.Items);
        Assert.Null(queue.Running);

        queue.Resume("big");
        await queue.RunAsync((item, token) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task AFailedModelIsPausedWithItsReasonAndTheRestCarryOn()
    {
        DownloadQueue queue = new(File);
        queue.Enqueue("broken", "fp32");
        queue.Enqueue("fine", "fp32");

        await queue.RunAsync(
            (item, token) => item.Model == "broken" ? throw new InvalidDataException("arrived damaged") : Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal([new("broken", "fp32", Paused: true, Failure: "arrived damaged")], queue.Items);
    }

    [Fact]
    public void AQueueFileThatDoesNotReadStartsEmpty()
    {
        Directory.CreateDirectory(root);
        System.IO.File.WriteAllText(File, "{ not json");

        Assert.Empty(new DownloadQueue(File).Items);
    }
}
