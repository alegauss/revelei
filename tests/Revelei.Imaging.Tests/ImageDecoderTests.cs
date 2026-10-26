using SharpGen.Runtime.Win32;
using Vortice.WIC;

namespace Revelei.Imaging.Tests;

/// <summary>
/// PB17: WIC decodes into RgbaImage upright, at the source depth, with alpha and grayscale handled.
/// PNGs are written here through WIC's encoder; the EXIF JPEG and the WebP are committed fixtures
/// (tests/fixtures/images/README.md says how they were made).
/// </summary>
public sealed class ImageDecoderTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "pb-decode-" + Guid.NewGuid().ToString("N"));

    public ImageDecoderTests() => Directory.CreateDirectory(scratch);

    public void Dispose() => Directory.Delete(scratch, recursive: true);

    private static string Fixture(string name) => Path.Combine(Repository.Root, "tests", "fixtures", "images", name);

    [Fact]
    public void AnExifRotatedJpegComesOutUpright()
    {
        RgbaImage image = ImageDecoder.Decode(Fixture("rotated-exif6.jpg"));

        Assert.Equal(8, image.Width);
        Assert.Equal(16, image.Height);
        AssertNear(Pixel(image, 4, 3), 65535, 0, 0);   // top half red
        AssertNear(Pixel(image, 4, 12), 0, 0, 65535);  // bottom half blue
        Assert.Equal(8, image.SourceBitDepth);
    }

    [Fact]
    public void A16BitPngKeeps16BitSamples()
    {
        ushort[] rgba = [1, 2, 3, 65535, 40000, 12345, 65534, 1000];
        string path = WritePng(2, 1, PixelFormat.Format64bppRGBA, MemoryMarshalBytes(rgba), 16);

        RgbaImage image = ImageDecoder.Decode(path);

        Assert.Equal(16, image.SourceBitDepth);
        Assert.Equal(rgba, image.Samples.ToArray());
        Assert.True(image.HasTransparency);
    }

    [Fact]
    public void A48BitPngIsOpaqueAt16Bits()
    {
        ushort[] rgb = [100, 200, 300];
        string path = WritePng(1, 1, PixelFormat.Format48bppRGB, MemoryMarshalBytes(rgb), 6);

        RgbaImage image = ImageDecoder.Decode(path);

        Assert.Equal([100, 200, 300, 65535], image.Samples.ToArray());
        Assert.Equal(16, image.SourceBitDepth);
    }

    [Fact]
    public void AGrayscalePngFillsAllThreeChannels()
    {
        string path = WritePng(3, 1, PixelFormat.Format8bppGray, [0, 128, 255], 3);

        RgbaImage image = ImageDecoder.Decode(path);

        Assert.Equal(8, image.SourceBitDepth);
        for (int x = 0; x < 3; x++)
        {
            ushort[] p = Pixel(image, x, 0);
            Assert.Equal(p[0], p[1]);
            Assert.Equal(p[0], p[2]);
            Assert.Equal(RgbaImage.Max, p[3]);
        }

        Assert.Equal(RgbaImage.From8(128), Pixel(image, 1, 0)[0]);
    }

    [Fact]
    public void An8BitPngWithAlphaKeepsItsAlpha()
    {
        // WIC's PNG encoder takes 8-bit alpha as BGRA, not RGBA.
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 64];
        string path = WritePng(2, 1, PixelFormat.Format32bppBGRA, bgra, 8);

        RgbaImage image = ImageDecoder.Decode(path);

        Assert.Equal([RgbaImage.Max, 0, 0, RgbaImage.Max], Pixel(image, 0, 0));
        Assert.Equal([0, RgbaImage.Max, 0, RgbaImage.From8(64)], Pixel(image, 1, 0));
    }

    [Fact]
    public void ALosslessWebpDecodesWithAlpha()
    {
        RgbaImage image = ImageDecoder.Decode(Fixture("alpha-lossless.webp"));

        Assert.Equal((4, 2), (image.Width, image.Height));
        Assert.Equal([RgbaImage.Max, 0, 0, RgbaImage.Max], Pixel(image, 0, 1));
        Assert.Equal(RgbaImage.From8(128), Pixel(image, 1, 0)[3]);
        Assert.Equal(0, Pixel(image, 3, 0)[3]);
    }

    [Fact]
    public void ADisplayP3ImageIsConvertedToSrgb()
    {
        // PB44: P3 (100, 180, 120) under the fixture's profile is sRGB (64, 184, 115), computed from
        // the colorant matrices and confirmed by LittleCMS (make-profile-fixtures.py).
        RgbaImage image = ImageDecoder.Decode(Fixture("p3-swatch.png"));

        ushort[] p = Pixel(image, 4, 4);
        Assert.InRange(RgbaImage.To8(p[0]), 64 - 2, 64 + 2);
        Assert.InRange(RgbaImage.To8(p[1]), 184 - 2, 184 + 2);
        Assert.InRange(RgbaImage.To8(p[2]), 115 - 2, 115 + 2);
    }

    [Fact]
    public void AProfileWindowsCannotReadLeavesThePixelsAsTheyAre()
    {
        RgbaImage image = ImageDecoder.Decode(Fixture("broken-profile.png"));

        ushort[] p = Pixel(image, 4, 4);
        Assert.Equal((100, 180, 120), (RgbaImage.To8(p[0]), RgbaImage.To8(p[1]), RgbaImage.To8(p[2])));
    }

    [Fact]
    public void AFileThatIsNotAnImageIsRefused()
    {
        string path = Path.Combine(scratch, "notes.png");
        File.WriteAllText(path, "not an image");

        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(path));
    }

    [Theory]
    [InlineData(1, BitmapTransformOptions.Rotate0)]
    [InlineData(3, BitmapTransformOptions.Rotate180)]
    [InlineData(6, BitmapTransformOptions.Rotate90)]
    [InlineData(8, BitmapTransformOptions.Rotate270)]
    [InlineData(0, BitmapTransformOptions.Rotate0)]
    public void ExifOrientationsMapToWicTransforms(int orientation, BitmapTransformOptions expected) =>
        Assert.Equal(expected, ImageDecoder.Transform(orientation));

    private static ushort[] Pixel(RgbaImage image, int x, int y) => image.Row(y).Slice(x * 4, 4).ToArray();

    private static void AssertNear(ushort[] pixel, int r, int g, int b)
    {
        // JPEG at quality 100 still moves a pure colour by a few levels.
        const int Tolerance = 3 * 257;
        Assert.InRange(pixel[0], r - Tolerance, r + Tolerance);
        Assert.InRange(pixel[1], g - Tolerance, g + Tolerance);
        Assert.InRange(pixel[2], b - Tolerance, b + Tolerance);
    }

    private static byte[] MemoryMarshalBytes(ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private unsafe string WritePng(int width, int height, Guid format, byte[] pixels, int stride)
    {
        string path = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".png");
        using IWICImagingFactory factory = new();
        using IWICStream stream = factory.CreateStream(path, FileAccess.Write);
        using IWICBitmapEncoder encoder = factory.CreateEncoder(ContainerFormatGuids.Png);
        encoder.Initialize(stream, BitmapEncoderCacheOption.NoCache);
        using IWICBitmapFrameEncode frame = encoder.CreateNewFrame(out IPropertyBag2 properties);
        using (properties)
        {
            frame.Initialize(properties);
        }

        frame.SetSize((uint)width, (uint)height);
        Guid requested = format;
        frame.SetPixelFormat(ref requested);
        Assert.Equal(format, requested);
        fixed (byte* data = pixels)
        {
            frame.WritePixels((uint)height, (uint)stride, (uint)pixels.Length, data);
        }

        frame.Commit();
        encoder.Commit();
        return path;
    }
}
