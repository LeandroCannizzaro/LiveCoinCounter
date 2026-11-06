using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Controls;

/// <summary>Un riquadro sul mosaico: moneta "in verifica" (vista una volta) o "acquisita", con un'etichetta facoltativa.</summary>
public sealed record OverlayBox(Mosaic.RectF Box, bool Stable, string? Label = null, bool Highlight = false);

/// <summary>
/// Vista a tutto schermo del mosaico che cresce durante l'acquisizione (disegno nativo Android, a tessere), con i riquadri
/// delle monete, il contorno della zona inquadrata e una piccola anteprima della fotocamera. Pizzica per lo zoom,
/// trascina per spostarti, doppio tocco per tornare a "adatta tutto".
/// </summary>
public class MosaicView : View
{
    public MosaicCanvas? Source { get; set; }
    public IReadOnlyList<OverlayBox> Boxes { get; set; } = Array.Empty<OverlayBox>();
    public Quad? FrameQuad { get; set; }

    /// <summary>Superficie come piano di colore (<see cref="SurfaceColor"/>) con solo le monete in foto, invece della foto del tavolo.</summary>
    public SurfaceLook SurfaceStyle { get; set; } = SurfaceLook.Continuous;

    /// <summary>Colore ARGB del piano (vedi <see cref="MosaicCanvas.DefaultSurfaceColor"/>).</summary>
    public int SurfaceColor { get; set; } = MosaicCanvas.DefaultSurfaceColor;

    /// <summary>Griglia metrica: pixel del mosaico per centimetro (0 = nessuna griglia) e i due livelli, in cm (0 = livello spento).</summary>
    public float GridPxPerCm { get; set; }
    public int GridMinorCm { get; set; }
    public int GridMajorCm { get; set; }

    /// <summary>Scritta della legenda della griglia (es. «Griglia 1 cm · scala stimata»).</summary>
    public string GridLabel { get; set; } = string.Empty;

    /// <summary>Dove si crede di essere (a stima) quando il tracking è perso, a tratteggio: l'inquadratura attuale rispetto alla superficie.</summary>
    public Quad? EstimatedQuad { get; set; }

    /// <summary>Ultima zona in cui il tracking era valido, mostrata (in arancione) solo quando la posizione è persa.</summary>
    public Quad? GhostQuad { get; set; }

    /// <summary>Ultimo fotogramma della fotocamera (ridotto) e la sua rotazione per mostrarlo dritto.</summary>
    public ArgbImage? Preview { get; set; }
    public int PreviewRotation { get; set; }

    /// <summary>Testo mostrato quando il mosaico è vuoto.</summary>
    public string EmptyText { get; set; } = string.Empty;

    /// <summary>Ridisegna; <paramref name="changedTiles"/> null = ricarica tutte le tessere.</summary>
    public void Refresh(IReadOnlyList<TileKey>? changedTiles = null) => Handler?.Invoke(nameof(Refresh), changedTiles);

    public void ResetZoom() => Handler?.Invoke(nameof(ResetZoom));

    /// <summary>Svuota la vista (nuova sessione).</summary>
    public void Clear()
    {
        Source = null;
        Boxes = Array.Empty<OverlayBox>();
        FrameQuad = null;
        GhostQuad = null;
        EstimatedQuad = null;
        Preview = null;
        Handler?.Invoke(nameof(Clear));
    }
}
