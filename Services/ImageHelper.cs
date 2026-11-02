using Android.Graphics;

namespace LiveCoinCounter.Services;

public static class ImageHelper
{
    /// <summary>
    /// Ridimensiona (lato massimo) e ricomprime in JPEG per ridurre il peso della richiesta.
    /// Se qualcosa va storto restituisce i byte originali.
    /// </summary>
    public static byte[] ShrinkForUpload(byte[] original, int maxSide = 1280, int quality = 80)
    {
        try
        {
            using var bitmap = BitmapFactory.DecodeByteArray(original, 0, original.Length);
            if (bitmap is null) return original;

            var scale = Math.Min(1.0, (double)maxSide / Math.Max(bitmap.Width, bitmap.Height));
            using var scaled = scale < 1.0
                ? Bitmap.CreateScaledBitmap(bitmap, (int)(bitmap.Width * scale), (int)(bitmap.Height * scale), true)
                : bitmap;

            using var output = new MemoryStream();
            scaled!.Compress(Bitmap.CompressFormat.Jpeg!, quality, output);
            return output.ToArray();
        }
        catch
        {
            return original;
        }
    }
}
