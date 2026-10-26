using SharpGen.Runtime.Win32;
using SkiaSharp;
using Vortice.WIC;

namespace Revelei.Imaging;

/// <summary>The formats a result can be exported as.</summary>
public enum ImageFormat
{
    /// <summary>Lossless, alpha kept, 16 bits a channel when the source had them.</summary>
    Png,

    /// <summary>Lossy with a quality setting; no alpha, so transparency is flattened.</summary>
    Jpeg,

    /// <summary>Lossy or lossless, alpha kept.</summary>
    Webp,
}

/// <summary>How to write an export.</summary>
/// <param name="Format">The file format.</param>
/// <param name="Quality">1 to 100, for JPEG and lossy WebP.</param>
/// <param name="Lossless">WebP only: write it lossless.</param>
/// <param name="Background">The colour transparency is flattened onto for JPEG, as 8-bit R, G, B.</param>
/// <param name="MetadataFrom">A source file whose camera details to keep (PB45); JPEG only, see <see cref="ImageEncoder.CarriesMetadata"/>.</param>
public sealed record EncodeOptions(ImageFormat Format, int Quality = 92, bool Lossless = false, (byte R, byte G, byte B)? Background = null, string? MetadataFrom = null);

/// <summary>
/// Writes an <see cref="RgbaImage"/> to a file (PB25). PNG and JPEG go through WIC, Windows' own
/// codecs; WIC has no WebP encoder (WINCODEC_ERR_COMPONENTNOTFOUND on the PB25 desk), so WebP goes
/// through SkiaSharp, MIT, used for nothing else. No metadata is written: the pixels are already
/// upright and in sRGB, which is what a viewer assumes without any.
/// </summary>
public static class ImageEncoder
{
    /// <summary>White, what transparency becomes in a JPEG unless the user picks otherwise.</summary>
    public static readonly (byte R, byte G, byte B) DefaultBackground = (255, 255, 255);

    /// <summary>
    /// The tags kept (PB45), relative to the EXIF tree: camera and lens, exposure, the dates, who
    /// made it and its copyright, and GPS. Never Orientation (written as 1, the pixels are upright),
    /// never the thumbnail IFD (it shows the original), never the maker notes.
    /// </summary>
    private static readonly string[] KeptTags =
    [
        "/{ushort=271}", "/{ushort=272}", "/{ushort=306}", "/{ushort=315}", "/{ushort=33432}",
        "/exif/{ushort=36867}", "/exif/{ushort=36868}", "/exif/{ushort=33434}", "/exif/{ushort=33437}",
        "/exif/{ushort=34855}", "/exif/{ushort=37386}", "/exif/{ushort=42035}", "/exif/{ushort=42036}",
        "/gps/{ushort=0}", "/gps/{ushort=1}", "/gps/{ushort=2}", "/gps/{ushort=3}", "/gps/{ushort=4}",
        "/gps/{ushort=5}", "/gps/{ushort=6}", "/gps/{ushort=7}", "/gps/{ushort=29}",
    ];

    /// <summary>
    /// Whether a format can keep the source's camera details. JPEG only: WIC's PNG encoder refuses
    /// EXIF trees (WINCODEC_ERR_UNEXPECTEDMETADATATYPE on the PB45 desk) and SkiaSharp writes no EXIF.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>True for JPEG.</returns>
    public static bool CarriesMetadata(ImageFormat format) => format == ImageFormat.Jpeg;

    /// <summary>The format a file name asks for.</summary>
    /// <param name="path">The file name.</param>
    /// <returns>The format, or null for an extension none of them use.</returns>
    public static ImageFormat? FormatFor(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".PNG" => ImageFormat.Png,
        ".JPG" or ".JPEG" => ImageFormat.Jpeg,
        ".WEBP" => ImageFormat.Webp,
        _ => null,
    };

    /// <summary>Whether writing the image in this format would drop transparency, which the UI warns about.</summary>
    /// <param name="image">The image.</param>
    /// <param name="format">The format.</param>
    /// <returns>True for a JPEG of an image with any transparent pixel.</returns>
    public static bool LosesTransparency(RgbaImage image, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        return format == ImageFormat.Jpeg && image.HasTransparency;
    }

    /// <summary>Writes the file, replacing one already there.</summary>
    /// <param name="image">The image.</param>
    /// <param name="path">The file.</param>
    /// <param name="options">Format and quality.</param>
    public static void Save(RgbaImage image, string path, EncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Quality, 100);

        // Written beside the target and moved over it, so a failed export never leaves half a file.
        string partial = path + ".partial";
        try
        {
            switch (options.Format)
            {
                case ImageFormat.Png:
                    SavePng(image, partial);
                    break;
                case ImageFormat.Jpeg:
                    SaveJpeg(image, partial, options);
                    break;
                case ImageFormat.Webp:
                    SaveWebp(image, partial, options);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.Format, "Unknown format.");
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static void SavePng(RgbaImage image, string path)
    {
        bool alpha = image.HasTransparency;
        if (image.SourceBitDepth == 16)
        {
            int channels = alpha ? 4 : 3;
            ushort[] samples = Pack16(image, channels);
            byte[] bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            WriteWic(path, ContainerFormatGuids.Png, alpha ? PixelFormat.Format64bppRGBA : PixelFormat.Format48bppRGB, image.Width, image.Height, bytes, image.Width * channels * 2, null, null);
        }
        else
        {
            WriteWic(path, ContainerFormatGuids.Png, alpha ? PixelFormat.Format32bppBGRA : PixelFormat.Format24bppBGR, image.Width, image.Height, Pack8Bgr(image, alpha, null), image.Width * (alpha ? 4 : 3), null, null);
        }
    }

    private static void SaveJpeg(RgbaImage image, string path, EncodeOptions options)
    {
        byte[] bgr = Pack8Bgr(image, alpha: false, options.Background ?? DefaultBackground);
        WriteWic(path, ContainerFormatGuids.Jpeg, PixelFormat.Format24bppBGR, image.Width, image.Height, bgr, image.Width * 3, options.Quality / 100f, options.MetadataFrom);
    }

    /// <summary>Copies the kept tags from a JPEG (/app1/ifd) or TIFF-style (/ifd) source into a JPEG frame.</summary>
    private static void CopyMetadata(IWICImagingFactory factory, string source, IWICMetadataQueryWriter writer)
    {
        using IWICBitmapDecoder decoder = factory.CreateDecoderFromFileName(source, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using IWICBitmapFrameDecode frame = decoder.GetFrame(0);
        IWICMetadataQueryReader reader;
        try
        {
            reader = frame.MetadataQueryReader;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return; // A source with no metadata at all.
        }

        using (reader)
        {
            foreach (string tag in KeptTags)
            {
                foreach (string root in (string[])["/app1/ifd", "/ifd"])
                {
                    try
                    {
                        writer.SetMetadataByName("/app1/ifd" + tag, reader.GetMetadataByName(root + tag));
                        break;
                    }
                    catch (SharpGen.Runtime.SharpGenException)
                    {
                        // Not under this root, or a type the writer refuses: try the next, then skip it.
                    }
                }
            }
        }

        writer.SetMetadataByName("/app1/ifd/{ushort=274}", new Variant { Value = (ushort)1 });
    }

    private static void SaveWebp(RgbaImage image, string path, EncodeOptions options)
    {
        SKImageInfo info = new(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap bitmap = new(info);
        Span<byte> pixels = bitmap.GetPixelSpan();
        ReadOnlySpan<ushort> samples = image.Samples;
        for (int i = 0; i < samples.Length; i++)
        {
            pixels[i] = RgbaImage.To8(samples[i]);
        }

        using SKPixmap pixmap = bitmap.PeekPixels();
        SKWebpEncoderOptions webp = new(options.Lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, options.Quality);
        using SKData data = pixmap.Encode(webp) ?? throw new IOException("SkiaSharp could not encode the WebP.");
        using FileStream file = File.Create(path);
        data.SaveTo(file);
    }

    private static ushort[] Pack16(RgbaImage image, int channels)
    {
        ReadOnlySpan<ushort> samples = image.Samples;
        ushort[] packed = new ushort[image.Width * image.Height * channels];
        for (int p = 0, o = 0; p < samples.Length; p += 4, o += channels)
        {
            samples.Slice(p, channels).CopyTo(packed.AsSpan(o, channels));
        }

        return packed;
    }

    /// <summary>8-bit BGR or BGRA; with a background, alpha is composited onto it instead.</summary>
    private static byte[] Pack8Bgr(RgbaImage image, bool alpha, (byte R, byte G, byte B)? background)
    {
        int channels = alpha ? 4 : 3;
        ReadOnlySpan<ushort> samples = image.Samples;
        byte[] packed = new byte[image.Width * image.Height * channels];
        for (int p = 0, o = 0; p < samples.Length; p += 4, o += channels)
        {
            float a = background is null ? 1f : samples[p + 3] / (float)RgbaImage.Max;
            (byte r, byte g, byte b) = background ?? (0, 0, 0);
            packed[o] = Over(samples[p + 2], b, a);
            packed[o + 1] = Over(samples[p + 1], g, a);
            packed[o + 2] = Over(samples[p], r, a);
            if (alpha)
            {
                packed[o + 3] = RgbaImage.To8(samples[p + 3]);
            }
        }

        return packed;
    }

    private static byte Over(ushort sample, byte background, float alpha) =>
        alpha >= 1f ? RgbaImage.To8(sample) : (byte)MathF.Round((sample / 257f * alpha) + (background * (1 - alpha)));

    private static unsafe void WriteWic(string path, Guid container, Guid format, int width, int height, byte[] pixels, int stride, float? quality, string? metadataFrom)
    {
        using IWICImagingFactory factory = new();
        using IWICStream stream = factory.CreateStream(path, FileAccess.Write);
        using IWICBitmapEncoder encoder = factory.CreateEncoder(container);
        encoder.Initialize(stream, BitmapEncoderCacheOption.NoCache);
        using IWICBitmapFrameEncode frame = encoder.CreateNewFrame(out IPropertyBag2 properties);
        using (properties)
        {
            if (quality is float q)
            {
                properties.Set("ImageQuality", q);
            }

            frame.Initialize(properties);
        }

        frame.SetSize((uint)width, (uint)height);
        Guid requested = format;
        frame.SetPixelFormat(ref requested);
        if (requested != format)
        {
            throw new InvalidOperationException($"WIC's encoder would not take {format}; it offered {requested}.");
        }

        if (metadataFrom is not null)
        {
            using IWICMetadataQueryWriter writer = frame.MetadataQueryWriter;
            CopyMetadata(factory, metadataFrom, writer);
        }

        fixed (byte* data = pixels)
        {
            frame.WritePixels((uint)height, (uint)stride, (uint)pixels.Length, data);
        }

        frame.Commit();
        encoder.Commit();
    }
}
