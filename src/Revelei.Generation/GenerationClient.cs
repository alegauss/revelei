using System.Diagnostics;
using System.Threading.Channels;
using Revelei.Imaging;

namespace Revelei.Generation;

/// <summary>
/// Generates images through Revelei.Generation.Host (PB78), a process of its own that owns the
/// native engine stable-diffusion.cpp (PB77, spike/diffusion/README.md). The host stays up
/// between images, keeping the model it loaded, since loading a 12 GB model takes longer than an
/// image. One generation runs at a time. A cancel asks the engine to stop after its current step
/// and, if it has not within <see cref="CancelGrace"/>, ends the host; a host that crashes or is
/// ended is started again by the next generation, so native code never takes the window down.
/// </summary>
public sealed class GenerationClient : IAsyncDisposable
{
    private readonly string hostPath;
    private readonly IReadOnlyList<string> hostArguments;
    private readonly SemaphoreSlim one = new(1, 1);
    private readonly Queue<string> errorTail = new();
    private Process? host;
    private Channel<HostMessage?>? messages;

    /// <summary>A client for a host executable.</summary>
    /// <param name="hostPath">Revelei.Generation.Host.exe.</param>
    /// <param name="hostArguments">Its arguments: --engine &lt;folder&gt; for the native build to load, or --fake for tests.</param>
    public GenerationClient(string hostPath, IReadOnlyList<string> hostArguments)
    {
        this.hostPath = hostPath;
        this.hostArguments = hostArguments;
    }

    /// <summary>How long a cancel may take before the host is ended.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Whether a host process is running now.</summary>
    public bool HostRunning => host is { HasExited: false };

    /// <summary>Generates one image.</summary>
    /// <param name="request">What to draw.</param>
    /// <param name="progress">Told each stage and step.</param>
    /// <param name="cancellation">Stops it: after the current step, or by ending the host.</param>
    /// <param name="preview">Given a low-resolution image of the result as it forms, after each sampling step.</param>
    /// <returns>The image, 8-bit and opaque.</returns>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    /// <exception cref="GenerationFailedException">The engine failed, or its host stopped.</exception>
    public async Task<RgbaImage> GenerateAsync(GenerationRequest request, IProgress<GenerationProgress>? progress, CancellationToken cancellation, IProgress<RgbaImage>? preview = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        await one.WaitAsync(cancellation).ConfigureAwait(false);
        string output = Path.Combine(Path.GetTempPath(), $"revelei-generated-{Guid.NewGuid():N}.rgba");
        try
        {
            (Process running, Channel<HostMessage?> inbox) = Start();
            await Send(running, new HostMessage("generate", request, output)).ConfigureAwait(false);

            using CancellationTokenRegistration asked = cancellation.Register(() => _ = CancelAsync(running));
            while (true)
            {
                HostMessage? message = await inbox.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                switch (message)
                {
                    case null:
                        cancellation.ThrowIfCancellationRequested();
                        throw new GenerationFailedException($"The generation engine stopped. {Tail()}".TrimEnd());
                    case { Type: "progress", Progress: { } step }:
                        progress?.Report(step);
                        break;
                    case { Type: "preview", Output: { } frame }:
                        if (preview is not null && File.Exists(frame))
                        {
                            preview.Report(Read(frame, message.Width, message.Height));
                        }

                        File.Delete(frame);
                        break;
                    case { Type: "done" }:
                        return Read(output, message.Width, message.Height);
                    case { Type: "failed", Cancelled: true }:
                        throw new OperationCanceledException(cancellation);
                    case { Type: "failed" }:
                        cancellation.ThrowIfCancellationRequested();
                        throw new GenerationFailedException(message.Message ?? "The engine failed without saying why.");
                }
            }
        }
        finally
        {
            File.Delete(output);
            foreach (string frame in Directory.EnumerateFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".preview*"))
            {
                File.Delete(frame); // A frame the host wrote before a crash or a cancel.
            }

            one.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (host is { HasExited: false } running)
        {
            running.StandardInput.Close();
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(5));
            try
            {
                await running.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                running.Kill(entireProcessTree: true);
            }
        }

        host?.Dispose();
        one.Dispose();
    }

    private (Process Host, Channel<HostMessage?> Inbox) Start()
    {
        if (host is { HasExited: false } alive && messages is { } open)
        {
            return (alive, open);
        }

        host?.Dispose();
        lock (errorTail)
        {
            errorTail.Clear();
        }

        ProcessStartInfo start = new(hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in hostArguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process started = Process.Start(start) ?? throw new GenerationFailedException($"{hostPath} did not start.");
        Channel<HostMessage?> inbox = Channel.CreateUnbounded<HostMessage?>();
        _ = Task.Run(async () =>
        {
            while (await started.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (HostMessage.Parse(line) is { } message)
                {
                    await inbox.Writer.WriteAsync(message).ConfigureAwait(false);
                }
            }

            await started.WaitForExitAsync().ConfigureAwait(false);
            await inbox.Writer.WriteAsync(null).ConfigureAwait(false);
        });
        started.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                lock (errorTail)
                {
                    errorTail.Enqueue(line);
                    while (errorTail.Count > 8)
                    {
                        errorTail.Dequeue();
                    }
                }
            }
        };
        started.BeginErrorReadLine();

        host = started;
        messages = inbox;
        return (started, inbox);
    }

    private static async Task Send(Process running, HostMessage message)
    {
        await running.StandardInput.WriteLineAsync(message.ToLine()).ConfigureAwait(false);
        await running.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    // Asks the engine to stop after its step, then ends the host if it has not.
    private async Task CancelAsync(Process running)
    {
        try
        {
            await Send(running, new HostMessage("cancel")).ConfigureAwait(false);
            if (!await ExitedWithin(running, CancelGrace).ConfigureAwait(false) && one.CurrentCount == 0)
            {
                running.Kill(entireProcessTree: true);
            }
        }
        catch (Exception gone) when (gone is IOException or InvalidOperationException)
        {
            // The host already stopped; the reader says so.
        }
    }

    // True when the host exits within the time; false when it is still running (busy or idle).
    private async Task<bool> ExitedWithin(Process running, TimeSpan time)
    {
        using CancellationTokenSource limit = new(time);
        try
        {
            while (one.CurrentCount == 0)
            {
                await Task.Delay(100, limit.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return running.HasExited;
        }
    }

    private string Tail()
    {
        lock (errorTail)
        {
            return string.Join(" ", errorTail);
        }
    }

    private static RgbaImage Read(string path, int width, int height)
    {
        byte[] pixels = File.ReadAllBytes(path);
        if (pixels.Length != width * height * 4)
        {
            throw new GenerationFailedException($"The engine wrote {pixels.Length} bytes for a {width} x {height} image.");
        }

        RgbaImage image = new(width, height);
        Span<ushort> samples = image.Samples;
        for (int i = 0; i < pixels.Length; i++)
        {
            samples[i] = (ushort)(pixels[i] * 257);
        }

        return image;
    }
}
