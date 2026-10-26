using System.Diagnostics;
using Revelei.Imaging;
using Revelei.Inference;
using Vortice.DXGI;

namespace Revelei.Bench;

/// <summary>PSNR and SSIM of one result against another, as export-and-quantize.py computes them (PB10).</summary>
internal static class Quality
{
    /// <summary>PSNR over the colour channels, or the alpha alone.</summary>
    /// <param name="a">One result.</param>
    /// <param name="b">The reference, the same size.</param>
    /// <param name="alphaOnly">Compare only the alpha.</param>
    /// <returns>Decibels; infinity when the two are equal.</returns>
    public static double Psnr(RgbaImage a, RgbaImage b, bool alphaOnly)
    {
        ReadOnlySpan<ushort> x = a.Samples;
        ReadOnlySpan<ushort> y = b.Samples;
        double sum = 0;
        long count = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if ((i % 4 == 3) == alphaOnly)
            {
                double d = (x[i] - y[i]) / (double)RgbaImage.Max;
                sum += d * d;
                count++;
            }
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(count / sum);
    }

    /// <summary>SSIM on the channel mean (or the alpha) with a uniform 7 x 7 window.</summary>
    /// <param name="a">One result.</param>
    /// <param name="b">The reference, the same size.</param>
    /// <param name="alphaOnly">Compare only the alpha.</param>
    /// <returns>The mean SSIM, 1 for identical images.</returns>
    public static double Ssim(RgbaImage a, RgbaImage b, bool alphaOnly)
    {
        const int k = 7;
        const double c1 = 0.01 * 0.01, c2 = 0.03 * 0.03;
        double[] x = Grey(a, alphaOnly), y = Grey(b, alphaOnly);
        int w = a.Width, h = a.Height;
        double[] xy = new double[x.Length], xx = new double[x.Length], yy = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            xy[i] = x[i] * y[i];
            xx[i] = x[i] * x[i];
            yy[i] = y[i] * y[i];
        }

        double[] sx = Table(x, w, h), sy = Table(y, w, h), sxy = Table(xy, w, h), sxx = Table(xx, w, h), syy = Table(yy, w, h);
        double total = 0;
        int windows = 0;
        for (int top = 0; top + k <= h; top++)
        {
            for (int left = 0; left + k <= w; left++)
            {
                double mx = Box(sx, w, left, top, k), my = Box(sy, w, left, top, k);
                double vx = Box(sxx, w, left, top, k) - (mx * mx), vy = Box(syy, w, left, top, k) - (my * my);
                double cov = Box(sxy, w, left, top, k) - (mx * my);
                total += ((2 * mx * my) + c1) * ((2 * cov) + c2) / (((mx * mx) + (my * my) + c1) * (vx + vy + c2));
                windows++;
            }
        }

        return windows == 0 ? 1 : total / windows;
    }

    private static double[] Grey(RgbaImage image, bool alphaOnly)
    {
        ReadOnlySpan<ushort> samples = image.Samples;
        double[] grey = new double[image.Width * image.Height];
        for (int p = 0; p < grey.Length; p++)
        {
            int i = p * 4;
            grey[p] = alphaOnly
                ? samples[i + 3] / (double)RgbaImage.Max
                : (samples[i] + samples[i + 1] + samples[i + 2]) / (3.0 * RgbaImage.Max);
        }

        return grey;
    }

    // A summed-area table one row and one column larger than the image.
    private static double[] Table(double[] values, int w, int h)
    {
        double[] sum = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            for (int x = 0; x < w; x++)
            {
                row += values[(y * w) + x];
                sum[((y + 1) * (w + 1)) + x + 1] = sum[(y * (w + 1)) + x + 1] + row;
            }
        }

        return sum;
    }

    private static double Box(double[] sum, int w, int left, int top, int k)
    {
        int stride = w + 1;
        double total = sum[((top + k) * stride) + left + k] - sum[(top * stride) + left + k] - sum[((top + k) * stride) + left] + sum[(top * stride) + left];
        return total / (k * k);
    }
}

/// <summary>What the process holds where inference runs: video memory on a GPU, private bytes on the CPU.</summary>
internal static class MemoryUse
{
    /// <summary>The current use.</summary>
    /// <param name="provider">Where inference runs.</param>
    /// <returns>Bytes.</returns>
    public static long Now(ProviderSelection provider)
    {
        if (provider.Provider != ExecutionProvider.Cpu && provider.Adapter is { } adapter
            && DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Success && factory is not null)
        {
            using (factory)
            {
                if (factory.EnumAdapters1((uint)adapter.Index, out IDXGIAdapter1? found).Success && found is not null)
                {
                    using (found)
                    using (IDXGIAdapter3 adapter3 = found.QueryInterface<IDXGIAdapter3>())
                    {
                        return (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
                    }
                }
            }
        }

        using Process self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    }

    /// <summary>Samples the use every 10 ms from construction until disposed and keeps the highest.</summary>
    internal sealed class Sampler : IDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly ProviderSelection provider;
        private readonly Task loop;
        private long peak;

        /// <summary>Starts sampling.</summary>
        /// <param name="provider">Where inference runs.</param>
        public Sampler(ProviderSelection provider)
        {
            this.provider = provider;
            peak = Now(provider);
            loop = Start();
        }

        /// <summary>The highest use seen so far.</summary>
        public long Peak => Math.Max(Interlocked.Read(ref peak), Now(provider));

        /// <summary>Stops sampling.</summary>
        public void Dispose()
        {
            stop.Cancel();
            loop.Wait();
            stop.Dispose();
        }

        private Task Start() => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                long now = Now(provider);
                long seen = Interlocked.Read(ref peak);
                if (now > seen)
                {
                    Interlocked.Exchange(ref peak, now);
                }

                try
                {
                    await Task.Delay(10, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }
}
