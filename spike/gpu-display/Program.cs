using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Revelei.Spike.GpuDisplay;

/// <summary>
/// PB40 spike: a 50 MP image on the GPU inside a WPF window, in a child HWND with a flip-model
/// swapchain (the host quickshell's QS4 chose). Usage: GpuDisplay [--warp] [--frames N].
/// Prints one key=value per measurement and exits; README.md records the runs.
/// </summary>
internal static class Program
{
    public const int ImageWidth = 8660;
    public const int ImageHeight = 5774;

    [STAThread]
    private static int Main(string[] args)
    {
        bool warp = args.Contains("--warp");
        int frames = args.SkipWhile(a => a != "--frames").Skip(1).Select(a => int.Parse(a, CultureInfo.InvariantCulture)).FirstOrDefault(600);
        Application app = new();
        Window window = new() { Title = "PB40 GPU display spike", Width = 1280, Height = 800, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        ChildHost host = new();
        window.Content = host;
        int exit = 0;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                double scale = window.GetDpi();
                using Renderer renderer = new(host.Child, (int)(host.ActualWidth * scale), (int)(host.ActualHeight * scale), warp);
                renderer.Run(frames);
            }
            catch (Exception failure)
            {
                Console.WriteLine($"error={failure.GetType().Name}: {failure.Message}");
                exit = 1;
            }

            window.Close();
        };
        app.Run(window);
        return exit;
    }

    private static double GetDpi(this Window window) => System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;

    public static void Report(string key, object value) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}={value}"));
}

/// <summary>A child HWND of the static class, which is all a swapchain needs to present into.</summary>
internal sealed class ChildHost : HwndHost
{
    public nint Child { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipSiblings = 0x04000000;
        Child = CreateWindowExW(0, "static", null, WsChild | WsVisible | WsClipSiblings, 0, 0, 1280, 800, hwndParent.Handle, 0, 0, 0);
        return new HandleRef(this, Child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint ex, string cls, string? name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
}

/// <summary>The device, the 50 MP texture with its mips, and the pan/zoom draw.</summary>
internal sealed class Renderer : IDisposable
{
    private const string Shader = """
        cbuffer View : register(b0) { float2 scale; float2 offset; };
        Texture2D image : register(t0);
        SamplerState linearMip : register(s0);
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V vs(uint id : SV_VertexID) {
            float2 corner = float2((id << 1) & 2, id & 2);
            V o; o.pos = float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = corner * scale + offset; return o;
        }
        float4 ps(V i) : SV_Target {
            if (any(i.uv < 0) || any(i.uv > 1)) return float4(0.1, 0.1, 0.1, 1);
            return image.Sample(linearMip, i.uv);
        }
        """;

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11RenderTargetView target;
    private readonly ID3D11Texture2D texture;
    private readonly ID3D11ShaderResourceView view;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11Buffer constants;
    private readonly int width;
    private readonly int height;

    public Renderer(nint hwnd, int width, int height, bool warp)
    {
        this.width = width;
        this.height = height;
        D3D11.D3D11CreateDevice(null, warp ? DriverType.Warp : DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device!, out context!).CheckError();

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIAdapter3 adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
        Program.Report("adapter", adapter.Description.Description);
        Program.Report("surface", $"{width}x{height}");
        long before = (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;

        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        swapChain = factory.CreateSwapChainForHwnd(device, hwnd, new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput, SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0), Scaling = Scaling.Stretch,
        });
        using (ID3D11Texture2D back = swapChain.GetBuffer<ID3D11Texture2D>(0))
        {
            target = device.CreateRenderTargetView(back);
        }

        byte[] pixels = Pattern();
        Stopwatch clock = Stopwatch.StartNew();
        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Program.ImageWidth, Height = Program.ImageHeight, MipLevels = 0, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        context.UpdateSubresource(pixels, texture, 0, (uint)(Program.ImageWidth * 4), 0);
        Finish();
        Program.Report("upload_ms", clock.Elapsed.TotalMilliseconds);

        view = device.CreateShaderResourceView(texture);
        clock.Restart();
        context.GenerateMips(view);
        Finish();
        Program.Report("generate_mips_ms", clock.Elapsed.TotalMilliseconds);
        long after = (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
        Program.Report("video_memory_added_mb", (after - before) / 1048576.0);

        ReadOnlyMemory<byte> vs = Compiler.Compile(Shader, "vs", "shader", "vs_5_0");
        ReadOnlyMemory<byte> ps = Compiler.Compile(Shader, "ps", "shader", "ps_5_0");
        vertex = device.CreateVertexShader(vs.Span);
        pixel = device.CreatePixelShader(ps.Span);
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        constants = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// <summary>A zoom from fit to 8x (1:1 is about 6.8x here) and back, panning across, at vsync and uncapped.</summary>
    public void Run(int frames)
    {
        for (int i = 0; i < 30; i++)
        {
            Draw(Path(i, frames), 1);
        }

        double[] times = new double[frames];
        Stopwatch clock = Stopwatch.StartNew();
        double last = 0;
        for (int i = 0; i < frames; i++)
        {
            Draw(Path(i, frames), 1);
            double now = clock.Elapsed.TotalMilliseconds;
            times[i] = now - last;
            last = now;
        }

        Array.Sort(times);
        Program.Report("vsync_frames", frames);
        Program.Report("vsync_median_ms", times[frames / 2]);
        Program.Report("vsync_p99_ms", times[(int)(frames * 0.99)]);
        Program.Report("vsync_max_ms", times[^1]);
        Program.Report("vsync_over_20ms", times.Count(t => t > 20));

        clock.Restart();
        for (int i = 0; i < frames; i++)
        {
            Draw(Path(i, frames), 0);
        }

        Finish();
        Program.Report("uncapped_fps", frames / clock.Elapsed.TotalSeconds);
    }

    private static (float Zoom, float X, float Y) Path(int i, int frames)
    {
        double t = (double)i / frames;
        float zoom = (float)(1 + (7 * Math.Sin(Math.PI * t)));
        return (zoom, (float)(0.5 + (0.3 * Math.Sin(2 * Math.PI * t))), (float)(0.5 + (0.3 * Math.Cos(2 * Math.PI * t))));
    }

    private void Draw((float Zoom, float X, float Y) at, uint sync)
    {
        // Fit the image to the surface, then zoom about (X, Y) in image coordinates.
        float imageAspect = (float)Program.ImageWidth / Program.ImageHeight;
        float surfaceAspect = (float)width / height;
        float sx = surfaceAspect > imageAspect ? surfaceAspect / imageAspect : 1;
        float sy = surfaceAspect > imageAspect ? 1 : imageAspect / surfaceAspect;
        float scaleX = sx / at.Zoom;
        float scaleY = sy / at.Zoom;
        float[] view4 = [scaleX, scaleY, at.X - (scaleX / 2), at.Y - (scaleY / 2)];
        MappedSubresource mapped = context.Map(constants, MapMode.WriteDiscard);
        Marshal.Copy(view4, 0, mapped.DataPointer, 4);
        context.Unmap(constants, 0);

        context.OMSetRenderTargets(target);
        context.RSSetViewport(new Viewport(width, height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.VSSetConstantBuffer(0, constants);
        context.PSSetShader(pixel);
        context.PSSetShaderResource(0, view);
        context.PSSetSampler(0, sampler);
        context.Draw(3, 0);
        swapChain.Present(sync, PresentFlags.None);
    }

    private void Finish()
    {
        using ID3D11Query query = device.CreateQuery(QueryType.Event);
        context.End(query);
        context.Flush();
        while (!context.IsDataAvailable(query))
        {
            Thread.SpinWait(100);
        }
    }

    /// <summary>A photo-like 50 MP pattern: gradients and a 64 px grid, so mips and aliasing show.</summary>
    private static byte[] Pattern()
    {
        byte[] pixels = new byte[Program.ImageWidth * Program.ImageHeight * 4];
        Parallel.For(0, Program.ImageHeight, y =>
        {
            int row = y * Program.ImageWidth * 4;
            for (int x = 0; x < Program.ImageWidth; x++)
            {
                bool grid = x % 64 == 0 || y % 64 == 0;
                pixels[row + (x * 4)] = grid ? (byte)255 : (byte)(x * 255 / Program.ImageWidth);
                pixels[row + (x * 4) + 1] = grid ? (byte)255 : (byte)(y * 255 / Program.ImageHeight);
                pixels[row + (x * 4) + 2] = grid ? (byte)255 : (byte)((x ^ y) & 0xFF);
                pixels[row + (x * 4) + 3] = 255;
            }
        });
        return pixels;
    }

    public void Dispose()
    {
        constants.Dispose();
        sampler.Dispose();
        pixel.Dispose();
        vertex.Dispose();
        view.Dispose();
        texture.Dispose();
        target.Dispose();
        swapChain.Dispose();
        context.Dispose();
        device.Dispose();
    }
}
