using Android.Graphics;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>Conversioni tra le immagini in codice puro (<see cref="ArgbImage"/>) e Android: Bitmap e JPEG.</summary>
public static class AndroidImaging
{
    public static Bitmap ToBitmap(ArgbImage image)
    {
        var bitmap = Bitmap.CreateBitmap(image.Width, image.Height, Bitmap.Config.Argb8888!)!;
        bitmap.SetPixels(image.Pixels, 0, image.Width, 0, 0, image.Width, image.Height);
        return bitmap;
    }

    public static ArgbImage FromBitmap(Bitmap bitmap)
    {
        var image = new ArgbImage(bitmap.Width, bitmap.Height);
        bitmap.GetPixels(image.Pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);
        return image;
    }

    public static byte[] ToJpeg(ArgbImage image, int quality = 88)
    {
        using var bitmap = ToBitmap(image);
        using var output = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Jpeg!, quality, output);
        return output.ToArray();
    }

    public static async Task SaveJpegAsync(ArgbImage image, string path, int quality = 88)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var bytes = ToJpeg(image, quality);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Decodifica un JPEG (raddrizzato secondo EXIF) riducendolo a lato massimo <paramref name="maxSide"/>.</summary>
    public static ArgbImage Decode(byte[] bytes, int maxSide = int.MaxValue)
    {
        using var upright = ImageHelper.DecodeUpright(bytes);
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(upright.Width, upright.Height));
        if (scale >= 1.0) return FromBitmap(upright);
        using var scaled = Bitmap.CreateScaledBitmap(upright, (int)(upright.Width * scale), (int)(upright.Height * scale), true)!;
        return FromBitmap(scaled);
    }

    /// <summary>Pixel RGBA (come da CameraX RGBA_8888) con passo di riga → ARGB.</summary>
    public static void RgbaToArgb(ReadOnlySpan<byte> rgba, int rowStride, int width, int height, int[] destination)
    {
        for (var y = 0; y < height; y++)
        {
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba.Slice(y * rowStride, width * 4));
            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                var v = row[x];   // little endian: A B G R
                destination[offset + x] = unchecked((int)((v & 0xFF00FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu)));
            }
        }
    }

    /// <summary>
    /// YUV_420_888 (immagine CPU di ARCore) → ARGB. Gestisce sia piani U/V separati sia interlacciati (pixelStride 2).
    /// </summary>
    public static ArgbImage YuvToArgb(ReadOnlySpan<byte> y, int yRowStride, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int uvRowStride, int uvPixelStride, int width, int height)
    {
        var image = new ArgbImage(width, height);
        var px = image.Pixels;
        for (var row = 0; row < height; row++)
        {
            var yRow = row * yRowStride;
            var uvRow = (row >> 1) * uvRowStride;
            for (var col = 0; col < width; col++)
            {
                int luma = y[yRow + col];
                var uvIndex = uvRow + (col >> 1) * uvPixelStride;
                int cb = u[uvIndex] - 128;
                int cr = v[uvIndex] - 128;
                var r = luma + ((91881 * cr) >> 16);
                var g = luma - ((22554 * cb + 46802 * cr) >> 16);
                var b = luma + ((116130 * cb) >> 16);
                px[row * width + col] = ImageOps.Argb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
            }
        }
        return image;
    }
}
