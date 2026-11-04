using Android.Graphics;
using Android.Media;
using LiveCoinCounter.Models;

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

    /// <summary>
    /// Decodifica la foto e la raddrizza secondo l'orientamento EXIF (la fotocamera salva spesso in orizzontale con un
    /// flag): il rilevatore, i riquadri e l'anteprima così usano tutti le stesse coordinate.
    /// </summary>
    public static Bitmap DecodeUpright(byte[] original)
    {
        var bitmap = BitmapFactory.DecodeByteArray(original, 0, original.Length)
            ?? throw new InvalidOperationException("Immagine non decodificabile.");

        var degrees = 0f;
        var flip = false;
        try
        {
            using var stream = new MemoryStream(original);
            var exif = new ExifInterface(stream);
            switch (exif.GetAttributeInt(ExifInterface.TagOrientation, 1))
            {
                case 3: degrees = 180; break;
                case 6: degrees = 90; break;
                case 8: degrees = 270; break;
                case 2: flip = true; break;
                case 4: degrees = 180; flip = true; break;
                case 5: degrees = 90; flip = true; break;
                case 7: degrees = 270; flip = true; break;
            }
        }
        catch
        {
            // senza EXIF leggibile si usa l'immagine così com'è
        }

        if (degrees == 0 && !flip) return bitmap;

        using var matrix = new Matrix();
        if (flip) matrix.PostScale(-1, 1);
        matrix.PostRotate(degrees);
        var rotated = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true)!;
        bitmap.Dispose();
        return rotated;
    }

    /// <summary>Disegna sulla foto un riquadro per ogni moneta rilevata (verde sicura, arancione dubbia) con taglio e confidenza.</summary>
    public static byte[] DrawDetections(byte[] original, IReadOnlyList<DetectedCoin> detections, float lowConfidence = 0.55f)
    {
        try
        {
            using var bitmap = DecodeUpright(original);
            using var canvasBitmap = bitmap.Copy(Bitmap.Config.Argb8888!, true)!;
            using var canvas = new Canvas(canvasBitmap);

            var unit = Math.Max(canvasBitmap.Width, canvasBitmap.Height) / 400f;
            using var box = new Android.Graphics.Paint { AntiAlias = true, StrokeWidth = Math.Max(2f, unit * 1.5f) };
            box.SetStyle(Android.Graphics.Paint.Style.Stroke);
            using var text = new Android.Graphics.Paint { AntiAlias = true, TextSize = Math.Max(14f, unit * 9f), FakeBoldText = true };
            using var back = new Android.Graphics.Paint { AntiAlias = true };

            foreach (var d in detections)
            {
                var color = d.Confidence < lowConfidence ? Android.Graphics.Color.Rgb(255, 152, 0) : Android.Graphics.Color.Rgb(46, 204, 113);
                box.Color = color;
                canvas.DrawRect(d.X, d.Y, d.X + d.Width, d.Y + d.Height, box);

                var label = $"{(d.ValueInCents >= 100 ? $"{d.ValueInCents / 100}€" : $"{d.ValueInCents}c")} {d.Confidence:P0}";
                var width = text.MeasureText(label) + 8;
                back.Color = color;
                canvas.DrawRect(d.X, d.Y - text.TextSize - 6, d.X + width, d.Y, back);
                text.Color = Android.Graphics.Color.Black;
                canvas.DrawText(label, d.X + 4, d.Y - 6, text);
            }

            using var output = new MemoryStream();
            canvasBitmap.Compress(Bitmap.CompressFormat.Jpeg!, 88, output);
            return output.ToArray();
        }
        catch
        {
            return original;
        }
    }

    /// <summary>
    /// Il mosaico di una sessione con il riquadro di ogni moneta e il suo valore (verde sicura, arancione incerta,
    /// blu corretta a mano, grigio esclusa). Null se la sessione non ha un mosaico salvato.
    /// </summary>
    public static byte[]? DrawSessionMosaic(LiveCoinCounter.Sessions.SessionRecord session, IReadOnlyList<LiveCoinCounter.Sessions.CoinRecord> coins)
    {
        if (session.MosaicPreviewPath is not { } path || !File.Exists(path)) return null;
        try
        {
            using var decoded = BitmapFactory.DecodeFile(path);
            if (decoded is null) return null;
            using var bitmap = decoded.Copy(Bitmap.Config.Argb8888!, true)!;
            using var canvas = new Canvas(bitmap);
            var unit = Math.Max(bitmap.Width, bitmap.Height) / 400f;
            using var box = new Android.Graphics.Paint { AntiAlias = true, StrokeWidth = Math.Max(2f, unit * 1.2f) };
            box.SetStyle(Android.Graphics.Paint.Style.Stroke);
            using var text = new Android.Graphics.Paint { AntiAlias = true, TextSize = Math.Max(12f, unit * 7f), FakeBoldText = true, Color = Android.Graphics.Color.Black };
            using var back = new Android.Graphics.Paint { AntiAlias = true };

            foreach (var c in coins)
            {
                var color = c.State switch
                {
                    LiveCoinCounter.Sessions.CoinState.Rejected => Android.Graphics.Color.Rgb(150, 150, 150),
                    LiveCoinCounter.Sessions.CoinState.Corrected => Android.Graphics.Color.Rgb(33, 150, 243),
                    _ when c.IsUncertain => Android.Graphics.Color.Rgb(255, 152, 0),
                    _ => Android.Graphics.Color.Rgb(46, 204, 113)
                };
                var s = session.PreviewScale;
                var x = (float)((c.X - session.PreviewOriginX) * s);
                var y = (float)((c.Y - session.PreviewOriginY) * s);
                var w = c.Width * s;
                var h = c.Height * s;
                box.Color = color;
                canvas.DrawRect(x, y, x + w, y + h, box);

                if (c.EffectiveCents is not { } cents) continue;
                var label = cents >= 100 ? $"{cents / 100}€" : $"{cents}c";
                var width = text.MeasureText(label) + 6;
                back.Color = color;
                canvas.DrawRect(x, y - text.TextSize - 4, x + width, y, back);
                canvas.DrawText(label, x + 3, y - 4, text);
            }

            using var output = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 88, output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Piccola immagine bianca usata per verificare la connessione a un provider.</summary>
    public static byte[] CreateTestImage()
    {
        using var bitmap = Bitmap.CreateBitmap(64, 64, Bitmap.Config.Argb8888!)!;
        bitmap.EraseColor(Android.Graphics.Color.White);
        using var output = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 80, output);
        return output.ToArray();
    }
}
