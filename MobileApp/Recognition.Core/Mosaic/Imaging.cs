namespace LiveCoinCounter.Mosaic;

/// <summary>Immagine a colori in memoria: un int ARGB per pixel, riga per riga (stesso formato di Android Bitmap.GetPixels).</summary>
public sealed class ArgbImage
{
    public ArgbImage(int width, int height, int[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Dimensioni non valide.");
        Width = width;
        Height = height;
        Pixels = pixels ?? new int[width * height];
        if (Pixels.Length != width * height) throw new ArgumentException("Numero di pixel diverso da larghezza x altezza.", nameof(pixels));
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public int this[int x, int y] => Pixels[y * Width + x];
}

/// <summary>Immagine in scala di grigi (un byte per pixel), usata per tracking e nitidezza.</summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public byte this[int x, int y] => Pixels[y * Width + x];
}

/// <summary>Rettangolo in coordinate pixel (angolo in alto a sinistra + dimensioni).</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width / 2;
    public float CenterY => Y + Height / 2;

    /// <summary>Diametro di una moneta inscritta: media dei due lati.</summary>
    public float Diameter => (Width + Height) / 2;

    public static RectF FromBounds(float left, float top, float right, float bottom) => new(left, top, right - left, bottom - top);

    public float Iou(RectF other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = Width * Height + other.Width * other.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

/// <summary>Operazioni sulle immagini in codice puro (niente librerie di piattaforma), così si provano nei test.</summary>
public static class ImageOps
{
    public const int LetterboxGray = 114;

    public static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    public static byte Luma(int argb) =>
        (byte)((((argb >> 16) & 0xFF) * 77 + ((argb >> 8) & 0xFF) * 150 + (argb & 0xFF) * 29) >> 8);

    /// <summary>Converte in grigio riducendo il lato maggiore a <paramref name="maxSide"/> (media sui blocchi). Restituisce anche il fattore di scala.</summary>
    public static GrayImage ToGray(ArgbImage image, int maxSide, out float scale)
    {
        var factor = Math.Max(1, (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)maxSide));
        var w = image.Width / factor;
        var h = image.Height / factor;
        var gray = new GrayImage(w, h);
        var src = image.Pixels;
        var area = factor * factor;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sum = 0;
            for (var dy = 0; dy < factor; dy++)
            {
                var row = (y * factor + dy) * image.Width + x * factor;
                for (var dx = 0; dx < factor; dx++) sum += Luma(src[row + dx]);
            }
            gray.Pixels[y * w + x] = (byte)(sum / area);
        }

        scale = 1f / factor;
        return gray;
    }

    /// <summary>Luminosità media (0–255) campionando un pixel ogni 8 in ogni direzione: basta per capire se c'è poca luce.</summary>
    public static double MeanBrightness(ArgbImage image)
    {
        long sum = 0;
        var n = 0;
        for (var y = 0; y < image.Height; y += 8)
        for (var x = 0; x < image.Width; x += 8)
        {
            sum += Luma(image.Pixels[y * image.Width + x]);
            n++;
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    /// <summary>Nitidezza: varianza del laplaciano (alta = a fuoco, bassa = mosso o sfocato).</summary>
    public static double Sharpness(GrayImage image)
    {
        var p = image.Pixels;
        var w = image.Width;
        double sum = 0, sumSq = 0;
        var n = 0;
        for (var y = 1; y < image.Height - 1; y += 2)
        for (var x = 1; x < w - 1; x += 2)
        {
            var i = y * w + x;
            double lap = p[i - 1] + p[i + 1] + p[i - w] + p[i + w] - 4 * p[i];
            sum += lap;
            sumSq += lap * lap;
            n++;
        }
        if (n == 0) return 0;
        var mean = sum / n;
        return sumSq / n - mean * mean;
    }

    /// <summary>Campionamento bilineare; fuori dall'immagine restituisce <paramref name="outside"/>.</summary>
    public static int SampleBilinear(ArgbImage image, double x, double y, int outside)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= image.Width - 1 || y0 >= image.Height - 1)
        {
            var xi = (int)Math.Round(x);
            var yi = (int)Math.Round(y);
            return xi >= 0 && yi >= 0 && xi < image.Width && yi < image.Height ? image[xi, yi] : outside;
        }

        var fx = x - x0;
        var fy = y - y0;
        var p = image.Pixels;
        var i = y0 * image.Width + x0;
        int a = p[i], b = p[i + 1], c = p[i + image.Width], d = p[i + image.Width + 1];

        int Channel(int shift)
        {
            var top = ((a >> shift) & 0xFF) * (1 - fx) + ((b >> shift) & 0xFF) * fx;
            var bottom = ((c >> shift) & 0xFF) * (1 - fx) + ((d >> shift) & 0xFF) * fx;
            return (int)(top * (1 - fy) + bottom * fy + 0.5);
        }

        return Argb(Channel(16), Channel(8), Channel(0));
    }

    /// <summary>
    /// Ritaglio quadrato centrato su <paramref name="box"/> con un margine, ridimensionato a <paramref name="size"/> px.
    /// Le parti fuori dall'immagine sono grigio 114 (come il letterbox di Ultralytics).
    /// </summary>
    public static ArgbImage CropSquare(ArgbImage image, RectF box, int size, float margin = 0.15f)
    {
        var side = Math.Max(box.Width, box.Height) * (1 + 2 * margin);
        var left = box.CenterX - side / 2;
        var top = box.CenterY - side / 2;
        var step = side / size;
        var outside = Argb(LetterboxGray, LetterboxGray, LetterboxGray);
        var crop = new ArgbImage(size, size);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            crop.Pixels[y * size + x] = SampleBilinear(image, left + (x + 0.5) * step - 0.5, top + (y + 0.5) * step - 0.5, outside);

        return crop;
    }

    /// <summary>Ridimensiona mantenendo le proporzioni in modo che il lato maggiore sia al massimo <paramref name="maxSide"/>.</summary>
    public static ArgbImage Downscale(ArgbImage image, int maxSide)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(image.Width, image.Height));
        if (scale >= 1.0) return image;
        var w = Math.Max(1, (int)(image.Width * scale));
        var h = Math.Max(1, (int)(image.Height * scale));
        var result = new ArgbImage(w, h);
        var sx = image.Width / (double)w;
        var sy = image.Height / (double)h;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            result.Pixels[y * w + x] = SampleBilinear(image, (x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5, 0);
        return result;
    }

    /// <summary>
    /// Disegna l'immagine in un quadrato <paramref name="inputSize"/> grigio 114, ridotta del fattore <paramref name="fill"/>
    /// (1 = letterbox classico, meno di 1 = immagine più piccola al centro). Restituisce i pixel e la trasformazione usata.
    /// </summary>
    public static int[] Letterbox(ArgbImage image, int inputSize, out Services.Recognition.Letterbox letterbox, float fill = 1f)
    {
        var scale = Math.Min((float)inputSize / image.Width, (float)inputSize / image.Height) * fill;
        var padX = (inputSize - image.Width * scale) / 2;
        var padY = (inputSize - image.Height * scale) / 2;
        letterbox = new Services.Recognition.Letterbox(scale, padX, padY);

        var outside = Argb(LetterboxGray, LetterboxGray, LetterboxGray);
        var pixels = new int[inputSize * inputSize];
        for (var y = 0; y < inputSize; y++)
        for (var x = 0; x < inputSize; x++)
        {
            var sx = (x + 0.5 - padX) / scale - 0.5;
            var sy = (y + 0.5 - padY) / scale - 0.5;
            pixels[y * inputSize + x] = sx < -0.5 || sy < -0.5 || sx > image.Width - 0.5 || sy > image.Height - 0.5
                ? outside
                : SampleBilinear(image, sx, sy, outside);
        }
        return pixels;
    }
}
