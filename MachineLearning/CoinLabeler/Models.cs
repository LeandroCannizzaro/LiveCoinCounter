using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoinLabeler;

/// <summary>Riquadro in pixel: origine in alto a sinistra.</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Area => W * H;

    public double Iou(Box other)
    {
        var x0 = Math.Max(X, other.X);
        var y0 = Math.Max(Y, other.Y);
        var x1 = Math.Min(X + W, other.X + other.W);
        var y1 = Math.Min(Y + H, other.Y + other.H);
        var inter = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

/// <summary>File JSON per immagine, conforme a coin-ml/schema/coin_label.schema.json.</summary>
public sealed class LabelFile
{
    public string Image { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Source { get; set; } = "own-phone";

    /// <summary>"auto" (i metodi concordano), "review" (da controllare) o "boxes-only" (nessuna AI usata).</summary>
    public string Status { get; set; } = "review";

    public LabelerInfo Labeler { get; set; } = new();
    public List<CoinLabel> Coins { get; set; } = new();
    public int TotalCents { get; set; }

    /// <summary>Monete coperte di grigio perché senza etichetta sicura (solo per le sessioni dell'app).</summary>
    public int? Masked { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class LabelerInfo
{
    public string Boxes { get; set; } = "hough";
    public List<string> Classifiers { get; set; } = new();
    public bool Reviewed { get; set; }
}

public sealed class CoinLabel
{
    /// <summary>Valore in centesimi; null se non classificata.</summary>
    public int? Label { get; set; }
    public double[] Bbox { get; set; } = Array.Empty<double>();
    public double? Confidence { get; set; }
    public bool? Agreement { get; set; }

    /// <summary>Voto di ogni provider per questa moneta (null = nessuna risposta utilizzabile).</summary>
    public Dictionary<string, int?>? Votes { get; set; }
}
