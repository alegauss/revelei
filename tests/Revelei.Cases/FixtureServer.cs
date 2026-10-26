using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Revelei.Cases;

/// <summary>
/// PB59: the download server a first-use case talks to, on this machine's loopback, so a case can
/// watch a download from the offer to the run without the network and without 267 MB. It serves
/// cases/fixtures/download under /files/, answers 404 under /missing/, and honours a Range request
/// as Hugging Face does. A whole file is sent no faster than <see cref="BytesPerSecond"/>, which
/// stretches the 770 KB stand-in to about 20 s, so a case has time to read the progress and pause
/// it however long the window takes to answer; a resumed one, asked for by Range, comes at full
/// speed, so what follows the resume fits in one step's wait. A raw socket rather than
/// HttpListener, which needs a URL reservation off an administrator's desk.
/// </summary>
public sealed class FixtureServer : IDisposable
{
    /// <summary>The port the committed manifests name (scripts/export-and-quantize.py's DOWNLOAD_PORT).</summary>
    public const int Port = 47623;

    /// <summary>How fast a whole file is sent; a Range request is not held back.</summary>
    public const int BytesPerSecond = 40 * 1024;

    private const int Chunk = 16 * 1024;

    private readonly string folder;
    private readonly TcpListener listener = new(IPAddress.Loopback, Port);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;

    /// <summary>Starts serving a folder.</summary>
    /// <param name="folder">The files /files/ serves.</param>
    public FixtureServer(string folder)
    {
        this.folder = folder;
        listener.Start();
        accepting = AcceptAsync(stopping.Token);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        try
        {
            accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends by its listener being stopped under it.
        }

        stopping.Dispose();
    }

    private async Task AcceptAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ended) when (ended is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = ServeAsync(client, stop);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                (string path, long? from) = await ReadRequestAsync(stream, stop).ConfigureAwait(false);
                string name = Path.GetFileName(path);
                string file = Path.Combine(folder, name);
                if (!path.StartsWith("/files/", StringComparison.Ordinal) || !File.Exists(file))
                {
                    await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", stop).ConfigureAwait(false);
                    return;
                }

                byte[] bytes = await File.ReadAllBytesAsync(file, stop).ConfigureAwait(false);
                long start = from is long asked && asked < bytes.Length ? asked : 0;

                string head = start > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{bytes.Length - 1}/{bytes.Length}\r\n")
                    : "HTTP/1.1 200 OK\r\n";
                await WriteAsync(stream, head + string.Create(CultureInfo.InvariantCulture, $"Content-Length: {bytes.Length - start}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n"), stop).ConfigureAwait(false);
                for (long at = start; at < bytes.Length; at += Chunk)
                {
                    int length = (int)Math.Min(Chunk, bytes.Length - at);
                    await stream.WriteAsync(bytes.AsMemory((int)at, length), stop).ConfigureAwait(false);
                    if (from is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds((double)length / BytesPerSecond), stop).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception gone) when (gone is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The app paused, or the suite ended: the connection going is the point.
            }
        }
    }

    private static async Task<(string Path, long? From)> ReadRequestAsync(NetworkStream stream, CancellationToken stop)
    {
        StringBuilder head = new();
        byte[] one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16 * 1024)
        {
            if (await stream.ReadAsync(one, stop).ConfigureAwait(false) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        string[] lines = head.ToString().Split("\r\n");
        string path = lines[0].Split(' ') is [_, var target, ..] ? target : "/";
        long? from = null;
        foreach (string line in lines)
        {
            const string Prefix = "range: bytes=";
            if (line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line[Prefix.Length..].Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start))
            {
                from = start;
            }
        }

        return (path, from);
    }

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken stop) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), stop).AsTask();
}
