using Vortice.WIC;

namespace Revelei.Imaging;

/// <summary>
/// Decodes an image file into an <see cref="RgbaImage"/> with WIC (PB17): the first frame,
/// upright per its EXIF orientation, converted to sRGB when it carries another colour profile,
/// with alpha kept and 16-bit samples kept at 16 bits. WIC brings every codec Windows has
/// (JPEG, PNG, WebP, TIFF, BMP, HEIF when installed) without a library of our own.
/// </summary>
public static class ImageDecoder
{
    // The EXIF Orientation tag (274) in each container's metadata tree.
    private static readonly string[] OrientationQueries = ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "/xmp/tiff:Orientation"];

    /// <summary>Decodes a file.</summary>
    /// <param name="path">The image file.</param>
    /// <returns>The pixels, upright and in sRGB.</returns>
    /// <exception cref="InvalidDataException">WIC has no codec that reads it.</exception>
    public static RgbaImage Decode(string path)
    {
        using IWICImagingFactory factory = new();
        IWICBitmapDecoder decoder;
        try
        {
            decoder = factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        }
        catch (SharpGen.Runtime.SharpGenException error)
        {
            throw new InvalidDataException($"{path} is not an image Windows can decode.", error);
        }

        using (decoder)
        using (IWICBitmapFrameDecode frame = decoder.GetFrame(0))
        {
            int depth = BitsPerChannel(factory, frame.PixelFormat) > 8 ? 16 : 8;
            using IWICColorTransform? toSrgb = ToSrgb(factory, frame);
            using IWICFormatConverter rgba = factory.CreateFormatConverter();
            rgba.Initialize((IWICBitmapSource?)toSrgb ?? frame, PixelFormat.Format64bppRGBA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            using IWICBitmapFlipRotator upright = factory.CreateBitmapFlipRotator();
            upright.Initialize(rgba, Transform(Orientation(frame)));

            Vortice.Mathematics.SizeI size = upright.Size;
            RgbaImage image = new(size.Width, size.Height, depth);
            Copy(upright, image);
            return image;
        }
    }

    /// <summary>The flip and rotation that turn an EXIF orientation (1 to 8) upright.</summary>
    /// <param name="orientation">The tag's value; anything else counts as 1.</param>
    /// <returns>WIC's transform.</returns>
    public static BitmapTransformOptions Transform(int orientation) => orientation switch
    {
        2 => BitmapTransformOptions.FlipHorizontal,
        3 => BitmapTransformOptions.Rotate180,
        4 => BitmapTransformOptions.FlipVertical,
        5 => BitmapTransformOptions.Rotate90 | BitmapTransformOptions.FlipHorizontal,
        6 => BitmapTransformOptions.Rotate90,
        7 => BitmapTransformOptions.Rotate270 | BitmapTransformOptions.FlipHorizontal,
        8 => BitmapTransformOptions.Rotate270,
        _ => BitmapTransformOptions.Rotate0,
    };

    private static int Orientation(IWICBitmapFrameDecode frame)
    {
        IWICMetadataQueryReader? reader;
        try
        {
            reader = frame.MetadataQueryReader;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return 1; // A format without metadata (BMP) has no reader.
        }

        if (reader is null)
        {
            return 1;
        }

        using (reader)
        {
            foreach (string query in OrientationQueries)
            {
                try
                {
                    object? value = reader.GetMetadataByName(query).Value;
                    if (value is not null && int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out int orientation))
                    {
                        return orientation;
                    }
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    // Not present in this container; try the next spelling.
                }
            }
        }

        return 1;
    }

    /// <summary>A transform to sRGB when the frame embeds another profile, else null (already sRGB).</summary>
    private static IWICColorTransform? ToSrgb(IWICImagingFactory factory, IWICBitmapFrameDecode frame)
    {
        IWICColorContext[] contexts = frame.TryGetColorContexts(factory) ?? [];
        try
        {
            IWICColorContext? embedded = contexts.FirstOrDefault(c => c.Type == ColorContextType.Profile);
            if (embedded is null)
            {
                return null;
            }

            using IWICColorContext srgb = factory.CreateColorContext();
            srgb.InitializeFromExifColorSpace(1);
            IWICColorTransform transform = factory.CreateColorTransformer();
            try
            {
                transform.Initialize(frame, embedded, srgb, PixelFormat.Format64bppRGBA);
                return transform;
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // A profile WIC cannot transform from is left as it is rather than failing the open.
                transform.Dispose();
                return null;
            }
        }
        finally
        {
            foreach (IWICColorContext context in contexts)
            {
                context.Dispose();
            }
        }
    }

    private static int BitsPerChannel(IWICImagingFactory factory, Guid format)
    {
        using IWICComponentInfo info = factory.CreateComponentInfo(format);
        using IWICPixelFormatInfo pixel = info.QueryInterface<IWICPixelFormatInfo>();
        return pixel.ChannelCount == 0 ? 8 : (int)(pixel.BitsPerPixel / pixel.ChannelCount);
    }

    private static unsafe void Copy(IWICBitmapSource source, RgbaImage image)
    {
        Span<ushort> samples = image.Samples;
        uint stride = (uint)(image.Width * 8);
        fixed (ushort* buffer = samples)
        {
            source.CopyPixels(new Vortice.Mathematics.RectI(0, 0, image.Width, image.Height), stride, (uint)(samples.Length * sizeof(ushort)), (nint)buffer);
        }
    }
}
