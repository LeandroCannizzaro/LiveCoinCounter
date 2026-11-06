using Android.Graphics;
using Android.Media;
using LiveCoinCounter.Models;
using LiveCoinCounter.Services.Sessions;

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
    public static byte[]? DrawSessionMosaic(LiveCoinCounter.Sessions.SessionRecord session, IReadOnlyList<LiveCoinCounter.Sessions.CoinRecord> coins,
        SurfaceRender? options = null)
    {
        options ??= new SurfaceRender();
        var path = options.Photo && File.Exists(SessionStorage.MosaicPhotoPath(session.Id)) ? SessionStorage.MosaicPhotoPath(session.Id) : session.MosaicPreviewPath;
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var decoded = BitmapFactory.DecodeFile(path);
            if (decoded is null) return null;
            using var bitmap = decoded.Copy(Bitmap.Config.Argb8888!, true)!;
            using var canvas = new Canvas(bitmap);
            var unit = Math.Max(bitmap.Width, bitmap.Height) / 400f;

            if (options.Grid != LiveCoinCounter.Sessions.GridSpacing.Off && options.Scale is { } scale)
                DrawGrid(canvas, bitmap.Width, bitmap.Height, session, scale, (int)options.Grid, unit);
            if (!options.Boxes) return Encode(bitmap);
            using var box = new Android.Graphics.Paint { AntiAlias = true, StrokeWidth = Math.Max(2f, unit * 1.2f) };
            box.SetStyle(Android.Graphics.Paint.Style.Stroke);
            using var text = new Android.Graphics.Paint { AntiAlias = true, TextSize = Math.Max(12f, unit * 7f), FakeBoldText = true, Color = Android.Graphics.Color.Black };
            using var back = new Android.Graphics.Paint { AntiAlias = true };

            foreach (var c in coins)
            {
                if (c.IsManual) continue;   // aggiunta a mano: non ha una posizione nel mosaico
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

                // il numero corrisponde a «Moneta N» nell'elenco: così si trova sul mosaico la moneta da correggere
                var value = c.EffectiveCents is { } cents ? (cents >= 100 ? $"{cents / 100}€" : $"{cents}c") : (c.State == LiveCoinCounter.Sessions.CoinState.Rejected ? "✗" : "?");
                var label = $"#{c.Number} {value}";
                var width = text.MeasureText(label) + 6;
                back.Color = color;
                canvas.DrawRect(x, y - text.TextSize - 4, x + width, y, back);
                canvas.DrawText(label, x + 3, y - 4, text);
            }

            return Encode(bitmap);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var output = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 90, output);
        return output.ToArray();
    }

    /// <summary>
    /// Griglia metrica sull'immagine del mosaico: ancorata all'origine del mosaico, una riga ogni <paramref name="cm"/> cm,
    /// più marcata ogni 5 righe (1 e 2 cm) o 2 (5 cm); in basso a sinistra la legenda con l'origine della scala.
    /// </summary>
    private static void DrawGrid(Canvas canvas, int width, int height, LiveCoinCounter.Sessions.SessionRecord session,
        LiveCoinCounter.Sessions.SurfaceScale scale, int cm, float unit)
    {
        var step = scale.PxPerMm * 10 * cm * session.PreviewScale;      // pixel dell'immagine per riga
        if (step < 4) return;
        var major = cm switch { 1 => 5, 2 => 5, 5 => 2, _ => 1 };
        using var line = new Android.Graphics.Paint { AntiAlias = true };
        line.SetStyle(Android.Graphics.Paint.Style.Stroke);

        // riga k del mosaico → pixel: (k * passoMosaico - origine) * scala dell'anteprima
        var stepMosaic = scale.PxPerMm * 10 * cm;
        var first = (int)Math.Floor(session.PreviewOriginX / stepMosaic);
        var last = (int)Math.Ceiling((session.PreviewOriginX + width / session.PreviewScale) / stepMosaic);
        for (var k = first; k <= last; k++)
        {
            var x = (float)((k * stepMosaic - session.PreviewOriginX) * session.PreviewScale);
            line.Color = Android.Graphics.Color.Argb(k % major == 0 ? 170 : 80, 255, 255, 255);
            line.StrokeWidth = Math.Max(1f, unit * (k % major == 0 ? 0.55f : 0.3f));
            canvas.DrawLine(x, 0, x, height, line);
        }
        first = (int)Math.Floor(session.PreviewOriginY / stepMosaic);
        last = (int)Math.Ceiling((session.PreviewOriginY + height / session.PreviewScale) / stepMosaic);
        for (var k = first; k <= last; k++)
        {
            var y = (float)((k * stepMosaic - session.PreviewOriginY) * session.PreviewScale);
            line.Color = Android.Graphics.Color.Argb(k % major == 0 ? 170 : 80, 255, 255, 255);
            line.StrokeWidth = Math.Max(1f, unit * (k % major == 0 ? 0.55f : 0.3f));
            canvas.DrawLine(0, y, width, y, line);
        }

        var label = $"Griglia {cm} cm · {scale.Label}";
        using var text = new Android.Graphics.Paint { AntiAlias = true, TextSize = Math.Max(12f, unit * 6f), Color = Android.Graphics.Color.White };
        using var back = new Android.Graphics.Paint { Color = Android.Graphics.Color.Argb(150, 0, 0, 0) };
        var w = text.MeasureText(label);
        canvas.DrawRect(unit * 2, height - text.TextSize - unit * 6, unit * 2 + w + unit * 4, height - unit * 2, back);
        canvas.DrawText(label, unit * 4, height - unit * 4, text);
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
