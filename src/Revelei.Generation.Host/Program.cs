using System.Collections.Concurrent;
using Revelei.Generation;
using Revelei.Generation.Host;

// PB78: reads Revelei.Generation's protocol on stdin, one JSON line a message, and answers on
// stdout. One worker thread owns the engine and runs the generations in order, so a cancel line
// is read while one runs. Closing stdin ends the host once the current generation is done.
//
//   Revelei.Generation.Host --engine <folder with stable-diffusion.dll and ggml-*.dll>
//   Revelei.Generation.Host --fake

Lock writing = new();
void Say(HostMessage message)
{
    lock (writing)
    {
        Console.Out.WriteLine(message.ToLine());
        Console.Out.Flush();
    }
}

using BlockingCollection<HostMessage> work = [];
IEngine? engine = null;
using ManualResetEventSlim ready = new();
Thread worker = new(() =>
{
    try
    {
        engine = args switch
        {
            ["--fake"] => new FakeEngine(),
            ["--engine", string folder] => new NativeEngine(folder),
            _ => throw new ArgumentException("Usage: Revelei.Generation.Host --engine <folder> | --fake"),
        };
    }
    catch (Exception failure) when (failure is ArgumentException or IOException or DllNotFoundException)
    {
        // The client reads this from stderr when the host stops.
        Console.Error.WriteLine(failure.Message);
        Environment.Exit(2);
    }

    ready.Set();
    using (engine)
    {
        foreach (HostMessage message in work.GetConsumingEnumerable())
        {
            try
            {
                int previews = 0;
                (int Width, int Height)? size = engine.Generate(
                    message.Request!,
                    message.Output!,
                    progress => Say(new HostMessage("progress", Progress: progress)),
                    (pixels, width, height) =>
                    {
                        // A file per frame, which the client reads and deletes, so none is overwritten mid-read.
                        string frame = $"{message.Output}.preview{++previews}";
                        File.WriteAllBytes(frame, pixels);
                        Say(new HostMessage("preview", Output: frame, Width: width, Height: height));
                    });
                Say(size is { } done
                    ? new HostMessage("done", Output: message.Output, Width: done.Width, Height: done.Height)
                    : new HostMessage("failed", Message: "Cancelled.", Cancelled: true));
            }
            catch (Exception failure) when (failure is GenerationFailedException or IOException or ArgumentException)
            {
                Say(new HostMessage("failed", Message: failure.Message));
            }
        }
    }
})
{
    Name = "Generation",
};
worker.Start();
ready.Wait();

while (Console.In.ReadLine() is { } line)
{
    switch (HostMessage.Parse(line))
    {
        case { Type: "cancel" }:
            engine!.Cancel();
            break;
        case { Type: "generate", Request: not null, Output: not null } generate:
            work.Add(generate);
            break;
    }
}

work.CompleteAdding();
worker.Join();
