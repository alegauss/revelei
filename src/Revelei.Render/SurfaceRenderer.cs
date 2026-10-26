using System.Runtime.InteropServices;
using Revelei.Imaging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Revelei.Render;

/// <summary>The colours the surface paints that are not the image's, handed in from the app's Brand.</summary>
/// <param name="Ground">Around the image.</param>
/// <param name="CheckerLight">Light squares behind transparency.</param>
/// <param name="CheckerDark">Dark squares behind transparency.</param>
/// <param name="Divider">The comparison divider.</param>
public readonly record struct SurfacePalette(Color4 Ground, Color4 CheckerLight, Color4 CheckerDark, Color4 Divider);

/// <summary>
/// Draws one or two images through D3D11 (PB27, on the PB40 spike's path): RGBA8 textures with
/// GPU mips sampled trilinearly, a divider or side-by-side comparison, a checkerboard behind
/// transparency. Renders into a window's flip swapchain, or into an offscreen texture that a test
/// reads back. A hardware device when one opens, WARP otherwise.
/// </summary>
public sealed class SurfaceRenderer : IDisposable
{
    private const string Shader = """
        cbuffer Frame : register(b0) {
            float2 spanA; float2 originA; float2 spanB; float2 originB;
            float2 surface; float divider; float mode;
            float4 ground; float4 checkerLight; float4 checkerDark; float4 dividerColour;
            float4 crop; float cropping; float3 unused;
        };
        Texture2D before : register(t0);
        Texture2D after : register(t1);
        SamplerState linearMip : register(s0);
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V vs(uint id : SV_VertexID) {
            float2 corner = float2((id << 1) & 2, id & 2);
            V o; o.pos = float4(corner * float2(2, -2) + float2(-1, 1), 0, 1); o.uv = corner; return o;
        }
        float4 shade(Texture2D image, float2 uv, float2 pixel) {
            if (any(uv < 0) || any(uv > 1)) return ground;
            float4 c = image.Sample(linearMip, uv);
            float4 square = ((uint(pixel.x) / 8 + uint(pixel.y) / 8) % 2 == 0) ? checkerLight : checkerDark;
            return float4(lerp(square.rgb, c.rgb, c.a), 1);
        }
        // PB67: outside the crop rectangle (photo uv, left-top and right-bottom) is dimmed towards
        // the ground; its border and eight handles are drawn in the divider's colour, measured in
        // surface pixels so they stay the same size at any zoom.
        float4 cropped(float2 uv, float2 pixel) {
            float4 c = shade(after, uv, pixel);
            float2 perPixel = spanA / surface;
            float2 fromLow = (uv - crop.xy) / perPixel, fromHigh = (crop.zw - uv) / perPixel;
            bool inside = all(fromLow >= 0) && all(fromHigh >= 0);
            bool spanX = fromLow.x > -1.5 && fromHigh.x > -1.5, spanY = fromLow.y > -1.5 && fromHigh.y > -1.5;
            if (((abs(fromLow.x) < 1 || abs(fromHigh.x) < 1) && spanY) || ((abs(fromLow.y) < 1 || abs(fromHigh.y) < 1) && spanX)) return dividerColour;
            float2 middle = (crop.xy + crop.zw) / 2;
            for (int hx = 0; hx < 3; hx++) {
                for (int hy = 0; hy < 3; hy++) {
                    float2 handle = float2(hx == 0 ? crop.x : hx == 1 ? middle.x : crop.z, hy == 0 ? crop.y : hy == 1 ? middle.y : crop.w);
                    if ((hx != 1 || hy != 1) && all(abs(uv - handle) / perPixel < 5)) return dividerColour;
                }
            }
            return inside ? c : float4(lerp(c.rgb, ground.rgb, 0.6), 1);
        }
        float4 ps(V i) : SV_Target {
            float2 pixel = i.pos.xy;
            if (cropping > 0.5) return cropped(i.uv * spanA + originA, pixel);
            if (mode > 1.5) {
                bool left = i.uv.x < 0.5;
                float2 half = float2(left ? i.uv.x * 2 : i.uv.x * 2 - 1, i.uv.y);
                return left ? shade(before, half * spanB + originB, pixel) : shade(after, half * spanB + originB, pixel);
            }
            if (mode > 0.5 && abs(pixel.x - divider * surface.x) < 1.5) return dividerColour;
            bool useBefore = mode > 0.5 && i.uv.x < divider;
            return useBefore ? shade(before, i.uv * spanA + originA, pixel) : shade(after, i.uv * spanA + originA, pixel);
        }
        """;

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11Buffer constants;
    private IDXGISwapChain1? swapChain;
    private ID3D11Texture2D? offscreen;
    private ID3D11RenderTargetView? target;
    private Layer? before;
    private Layer? after;

    /// <summary>Opens a device: hardware unless asked for WARP or none opens.</summary>
    /// <param name="warp">Force the software rasteriser, as tests do.</param>
    public SurfaceRenderer(bool warp = false)
    {
        if (warp || D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device!, out context!).Failure)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device!, out context!).CheckError();
            IsWarp = true;
        }

        vertex = device.CreateVertexShader(Compiler.Compile(Shader, "vs", "surface", "vs_5_0").Span);
        pixel = device.CreatePixelShader(Compiler.Compile(Shader, "ps", "surface", "ps_5_0").Span);
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        constants = device.CreateBuffer(new BufferDescription(160, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// <summary>Whether this is the software rasteriser.</summary>
    public bool IsWarp { get; }

    /// <summary>Surface pixels across.</summary>
    public int Width { get; private set; }

    /// <summary>Surface pixels down.</summary>
    public int Height { get; private set; }

    /// <summary>The after image's size, which the view is computed against; zero before any image.</summary>
    public (int Width, int Height) ImageSize => after is { } a ? (a.Width, a.Height) : (0, 0);

    /// <summary>Presents into a window, replacing any earlier target.</summary>
    /// <param name="hwnd">The child window.</param>
    /// <param name="width">Its pixels across.</param>
    /// <param name="height">Its pixels down.</param>
    public void AttachWindow(nint hwnd, int width, int height)
    {
        ReleaseTarget();
        swapChain?.Dispose();
        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        swapChain = factory.CreateSwapChainForHwnd(device, hwnd, new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height), Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0), Scaling = Scaling.Stretch,
        });
        (Width, Height) = (Math.Max(1, width), Math.Max(1, height));
        using ID3D11Texture2D back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        target = device.CreateRenderTargetView(back);
    }

    /// <summary>Follows the window's new size.</summary>
    /// <param name="width">Pixels across.</param>
    /// <param name="height">Pixels down.</param>
    public void Resize(int width, int height)
    {
        if (swapChain is null || (width == Width && height == Height) || width < 1 || height < 1)
        {
            return;
        }

        ReleaseTarget();
        swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        (Width, Height) = (width, height);
        using ID3D11Texture2D back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        target = device.CreateRenderTargetView(back);
    }

    /// <summary>Renders into an offscreen texture instead of a window, for tests.</summary>
    /// <param name="width">Pixels across.</param>
    /// <param name="height">Pixels down.</param>
    public void AttachOffscreen(int width, int height)
    {
        ReleaseTarget();
        offscreen?.Dispose();
        offscreen = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.RenderTarget,
        });
        (Width, Height) = (width, height);
        target = device.CreateRenderTargetView(offscreen);
    }

    /// <summary>Uploads the images; before may be null, which compares the after with itself.</summary>
    /// <param name="beforeImage">The original.</param>
    /// <param name="afterImage">The version shown.</param>
    public void SetImages(RgbaImage? beforeImage, RgbaImage afterImage)
    {
        ArgumentNullException.ThrowIfNull(afterImage);
        after?.Dispose();
        before?.Dispose();
        after = Upload(afterImage);
        before = beforeImage is null || ReferenceEquals(beforeImage, afterImage) ? null : Upload(beforeImage);
    }

    /// <summary>Draws a frame and presents it, or leaves it in the offscreen texture.</summary>
    /// <param name="view">Where the image sits.</param>
    /// <param name="mode">Single, divider or side by side.</param>
    /// <param name="divider">The divider, 0 to 1 across.</param>
    /// <param name="palette">The surface's own colours.</param>
    /// <param name="crop">The crop rectangle being edited, in the after image's pixels (PB67), or null.</param>
    public void Render(View view, CompareMode mode, double divider, SurfacePalette palette, PixelRect? crop = null)
    {
        if (target is null)
        {
            return;
        }

        context.ClearRenderTargetView(target, palette.Ground);
        if (after is { } a)
        {
            (double sx, double sy) = ViewMath.Span(a.Width, a.Height, Width, Height, view.Zoom);
            (double hx, double hy) = ViewMath.Span(a.Width, a.Height, Width / 2.0, Height, 1);
            PixelRect c = crop ?? new PixelRect(0, 0, a.Width, a.Height);
            float[] frame =
            [
                (float)sx, (float)sy, (float)(view.CenterX - (sx / 2)), (float)(view.CenterY - (sy / 2)),
                (float)hx, (float)hy, (float)(0.5 - (hx / 2)), (float)(0.5 - (hy / 2)),
                Width, Height, (float)divider, (float)mode,
                .. Floats(palette.Ground), .. Floats(palette.CheckerLight), .. Floats(palette.CheckerDark), .. Floats(palette.Divider),
                (float)c.X / a.Width, (float)c.Y / a.Height, (float)(c.X + c.Width) / a.Width, (float)(c.Y + c.Height) / a.Height,
                crop is null ? 0 : 1, 0, 0, 0,
            ];
            MappedSubresource mapped = context.Map(constants, MapMode.WriteDiscard);
            Marshal.Copy(frame, 0, mapped.DataPointer, frame.Length);
            context.Unmap(constants, 0);

            context.OMSetRenderTargets(target);
            context.RSSetViewport(new Viewport(Width, Height));
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vertex);
            context.PSSetShader(pixel);
            context.PSSetConstantBuffer(0, constants);
            context.PSSetShaderResources(0, [(before ?? a).View, a.View]);
            context.PSSetSampler(0, sampler);
            context.Draw(3, 0);
        }

        swapChain?.Present(1, PresentFlags.None);
    }

    /// <summary>Reads the offscreen texture back as BGRA bytes, row by row; tests only.</summary>
    /// <returns>Width × Height × 4 bytes.</returns>
    public byte[] ReadPixels()
    {
        ID3D11Texture2D source = offscreen ?? throw new InvalidOperationException("Not rendering offscreen.");
        Texture2DDescription description = source.Description with
        {
            BindFlags = BindFlags.None, Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        };
        using ID3D11Texture2D staging = device.CreateTexture2D(description);
        context.CopyResource(staging, source);
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), pixels, y * Width * 4, Width * 4);
        }

        context.Unmap(staging, 0);
        return pixels;
    }

    /// <summary>Releases every GPU object.</summary>
    public void Dispose()
    {
        before?.Dispose();
        after?.Dispose();
        ReleaseTarget();
        offscreen?.Dispose();
        swapChain?.Dispose();
        constants.Dispose();
        sampler.Dispose();
        pixel.Dispose();
        vertex.Dispose();
        context.Dispose();
        device.Dispose();
    }

    private static float[] Floats(Color4 c) => [c.R, c.G, c.B, c.A];

    private void ReleaseTarget()
    {
        context.ClearState();
        target?.Dispose();
        target = null;
    }

    /// <summary>An image as an RGBA8 texture with a full mip chain: the display is 8-bit (PB40).</summary>
    private Layer Upload(RgbaImage image)
    {
        byte[] bytes = new byte[image.Width * image.Height * 4];
        ReadOnlySpan<ushort> samples = image.Samples;
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = RgbaImage.To8(samples[i]);
        }

        ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)image.Width, Height = (uint)image.Height, MipLevels = 0, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        context.UpdateSubresource(bytes, texture, 0, (uint)(image.Width * 4), 0);
        ID3D11ShaderResourceView view = device.CreateShaderResourceView(texture);
        context.GenerateMips(view);
        return new Layer(texture, view, image.Width, image.Height);
    }

    private sealed record Layer(ID3D11Texture2D Texture, ID3D11ShaderResourceView View, int Width, int Height) : IDisposable
    {
        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }
}
