using SkiaSharp;

namespace CoinLabeler;

/// <summary>Lettura, orientamento, ridimensionamento e ritaglio delle immagini (SkiaSharp).</summary>
public static class ImageIO
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    /// <summary>
    /// Decodifica l'immagine applicando l'orientamento EXIF. Le coordinate dei box si riferiscono sempre
    /// all'immagine già orientata, quindi è questa la versione che viene salvata nel dataset.
    /// </summary>
    public static SKBitmap LoadOriented(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("Immagine non decodificabile.");
        var origin = codec.EncodedOrigin;
        var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("Immagine non decodificabile.");
        return Orient(bitmap, origin);
    }

    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? src.Height : src.Width;
        var h = swap ? src.Width : src.Height;

        var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(0, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(w, h); canvas.RotateDegrees(-90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, h); canvas.RotateDegrees(-90); break;
        }

        canvas.DrawBitmap(src, 0, 0);
        src.Dispose();
        return result;
    }

    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality) =>
        SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();

    /// <summary>Riduce il lato lungo a <paramref name="maxSide"/> pixel (se più grande) e ricomprime in JPEG.</summary>
    public static byte[] ShrinkForUpload(byte[] bytes, int maxSide = 1280, int quality = 80)
    {
        try
        {
            using var bitmap = LoadOriented(bytes);
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(bitmap.Width, bitmap.Height));
            if (scale >= 1.0) return EncodeJpeg(bitmap, quality);

            var info = new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale));
            using var scaled = bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return EncodeJpeg(scaled ?? bitmap, quality);
        }
        catch
        {
            return bytes;
        }
    }

    /// <summary>Ritaglia un riquadro con un margine relativo, limitato ai bordi dell'immagine.</summary>
    public static byte[] CropJpeg(SKBitmap bitmap, Box box, double padding = 0.15)
    {
        var pad = (int)(Math.Max(box.W, box.H) * padding);
        var x0 = Math.Max(0, (int)box.X - pad);
        var y0 = Math.Max(0, (int)box.Y - pad);
        var x1 = Math.Min(bitmap.Width, (int)(box.X + box.W) + pad);
        var y1 = Math.Min(bitmap.Height, (int)(box.Y + box.H) + pad);

        using var crop = new SKBitmap(x1 - x0, y1 - y0);
        using var canvas = new SKCanvas(crop);
        canvas.DrawBitmap(bitmap, new SKRect(x0, y0, x1, y1), new SKRect(0, 0, crop.Width, crop.Height));
        return EncodeJpeg(crop, 90);
    }
}
