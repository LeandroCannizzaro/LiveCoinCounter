namespace LiveCoinCounter.Mosaic;

public readonly record struct TileKey(int X, int Y);

/// <summary>Una tessera del mosaico: pixel ARGB e, per ogni pixel, il "peso" del fotogramma che l'ha scritto.</summary>
public sealed class MosaicTile
{
    public MosaicTile(TileKey key)
    {
        Key = key;
        Pixels = new int[MosaicCanvas.TileSize * MosaicCanvas.TileSize];
        Weights = new byte[MosaicCanvas.TileSize * MosaicCanvas.TileSize];
    }

    public TileKey Key { get; }
    public int[] Pixels { get; }
    public byte[] Weights { get; }

    /// <summary>Cresce a ogni modifica: la vista ridisegna solo le tessere cambiate.</summary>
    public int Version { get; internal set; }
}

/// <summary>
/// Il mosaico della superficie, diviso in tessere create solo dove c'è contenuto (anche a coordinate negative).
/// Ogni pixel del tavolo esiste una sola volta: dove i fotogrammi si sovrappongono vince quello con peso maggiore
/// (più nitido e con il punto più lontano dal bordo del fotogramma, dove l'ottica distorce meno). Thread-safe.
/// </summary>
public sealed class MosaicCanvas
{
    public const int TileSize = 256;

    private readonly Dictionary<TileKey, MosaicTile> _tiles = new();
    private readonly object _sync = new();

    public MosaicCanvas(int maxTiles = 400) => MaxTiles = maxTiles;

    public int MaxTiles { get; }

    /// <summary>Vero quando il mosaico ha raggiunto la dimensione massima: le parti nuove non vengono più aggiunte.</summary>
    public bool IsFull { get; private set; }

    public int TileCount { get { lock (_sync) return _tiles.Count; } }

    /// <summary>Rettangolo occupato dal contenuto, in pixel del mosaico (vuoto se non c'è ancora nulla).</summary>
    public RectF Bounds { get; private set; }

    /// <summary>
    /// Proietta un fotogramma nel mosaico con l'omografia fotogramma → mosaico.
    /// <paramref name="quality"/> (0..1) pesa i pixel: un fotogramma più nitido sostituisce uno meno nitido.
    /// Restituisce le tessere modificate.
    /// </summary>
    public IReadOnlyList<TileKey> Compose(ArgbImage frame, Homography frameToMosaic, float quality)
    {
        var quad = frameToMosaic.MapRect(frame.Width, frame.Height);
        if (!quad.IsConvex) return Array.Empty<TileKey>();

        var bounds = quad.Bounds;
        var inverse = frameToMosaic.Inverse();
        var changed = new List<TileKey>();
        var halfMin = Math.Min(frame.Width, frame.Height) / 2.0;
        quality = Math.Clamp(quality, 0.05f, 1f);

        int tx0 = FloorDiv((int)Math.Floor(bounds.X)), ty0 = FloorDiv((int)Math.Floor(bounds.Y));
        int tx1 = FloorDiv((int)Math.Ceiling(bounds.Right)), ty1 = FloorDiv((int)Math.Ceiling(bounds.Bottom));

        lock (_sync)
        {
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var key = new TileKey(tx, ty);
                var existing = _tiles.TryGetValue(key, out var tile);
                if (!existing)
                {
                    if (_tiles.Count >= MaxTiles) { IsFull = true; continue; }
                    tile = new MosaicTile(key);
                }

                var written = DrawInto(tile!, frame, inverse, quad, bounds, halfMin, quality);
                if (written == 0) continue;
                if (!existing) _tiles[key] = tile!;
                tile!.Version++;
                changed.Add(key);
            }

            if (changed.Count > 0) UpdateBounds(bounds);
        }

        return changed;
    }

    private static int DrawInto(MosaicTile tile, ArgbImage frame, Homography inverse, Quad quad, RectF bounds, double halfMin, float quality)
    {
        var originX = tile.Key.X * TileSize;
        var originY = tile.Key.Y * TileSize;
        var x0 = Math.Max(0, (int)Math.Floor(bounds.X) - originX);
        var y0 = Math.Max(0, (int)Math.Floor(bounds.Y) - originY);
        var x1 = Math.Min(TileSize - 1, (int)Math.Ceiling(bounds.Right) - originX);
        var y1 = Math.Min(TileSize - 1, (int)Math.Ceiling(bounds.Bottom) - originY);
        var written = 0;

        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            double mx = originX + x + 0.5, my = originY + y + 0.5;
            if (!quad.Contains(mx, my)) continue;
            var (fx, fy) = inverse.Apply(mx, my);
            if (fx < 0 || fy < 0 || fx >= frame.Width || fy >= frame.Height) continue;

            var edge = Math.Min(Math.Min(fx, fy), Math.Min(frame.Width - fx, frame.Height - fy)) / halfMin;
            var weight = (byte)Math.Clamp(1 + (int)(Math.Min(1, edge) * quality * 254), 1, 255);
            var i = y * TileSize + x;
            if (weight <= tile.Weights[i]) continue;

            tile.Weights[i] = weight;
            tile.Pixels[i] = ImageOps.SampleBilinear(frame, fx - 0.5, fy - 0.5, 0);
            written++;
        }
        return written;
    }

    private void UpdateBounds(RectF added)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) { Bounds = added; return; }
        Bounds = RectF.FromBounds(Math.Min(Bounds.X, added.X), Math.Min(Bounds.Y, added.Y),
            Math.Max(Bounds.Right, added.Right), Math.Max(Bounds.Bottom, added.Bottom));
    }

    public static int FloorDiv(int value) => (int)Math.Floor(value / (double)TileSize);

    /// <summary>Copia (sotto lock) delle tessere indicate, o di tutte: la vista le legge senza bloccare il mosaico.</summary>
    public List<(TileKey Key, int Version, int[] Pixels)> Snapshot(IEnumerable<TileKey>? keys = null)
    {
        lock (_sync)
        {
            var source = keys is null ? _tiles.Values : keys.Where(_tiles.ContainsKey).Select(k => _tiles[k]);
            return source.Select(t => (t.Key, t.Version, (int[])t.Pixels.Clone())).ToList();
        }
    }

    public IReadOnlyList<TileKey> Keys { get { lock (_sync) return _tiles.Keys.ToList(); } }

    /// <summary>Immagine unica del mosaico ridotta a lato massimo <paramref name="maxSide"/>. Le zone vuote sono nere.</summary>
    public ArgbImage? Render(int maxSide, out float scale, out PointD origin)
    {
        lock (_sync)
        {
            scale = 1;
            origin = default;
            if (_tiles.Count == 0) return null;

            // ritaglio sul contenuto effettivo, non sulla griglia delle tessere
            var minX = (int)Math.Floor(Bounds.X);
            var minY = (int)Math.Floor(Bounds.Y);
            var fullW = Math.Max(1, (int)Math.Ceiling(Bounds.Right) - minX);
            var fullH = Math.Max(1, (int)Math.Ceiling(Bounds.Bottom) - minY);
            scale = (float)Math.Min(1.0, maxSide / (double)Math.Max(fullW, fullH));
            var w = Math.Max(1, (int)(fullW * scale));
            var h = Math.Max(1, (int)(fullH * scale));
            origin = new PointD(minX, minY);

            var image = new ArgbImage(w, h);
            var black = ImageOps.Argb(0, 0, 0);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var mx = (int)((x + 0.5) / scale) + minX;
                var my = (int)((y + 0.5) / scale) + minY;
                var key = new TileKey(FloorDiv(mx), FloorDiv(my));
                if (!_tiles.TryGetValue(key, out var tile)) { image.Pixels[y * w + x] = black; continue; }
                var i = (my - key.Y * TileSize) * TileSize + (mx - key.X * TileSize);
                image.Pixels[y * w + x] = tile.Weights[i] == 0 ? black : tile.Pixels[i];
            }
            return image;
        }
    }
}

/// <summary>
/// Quali zone del mosaico sono già coperte, a celle di 16 px. Serve a decidere quando un fotogramma porta abbastanza
/// superficie nuova da diventare fotogramma chiave.
/// </summary>
public sealed class CoverageGrid
{
    public const int Cell = 16;
    private readonly HashSet<long> _covered = new();

    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    /// <summary>Frazione (0..1) dell'area del quadrilatero già coperta.</summary>
    public double CoveredFraction(Quad quad)
    {
        var (total, covered) = Count(quad, mark: false);
        return total == 0 ? 1 : (double)covered / total;
    }

    public void Mark(Quad quad) => Count(quad, mark: true);

    public int CellCount => _covered.Count;

    private (int Total, int Covered) Count(Quad quad, bool mark)
    {
        var b = quad.Bounds;
        int cx0 = (int)Math.Floor(b.X / Cell), cy0 = (int)Math.Floor(b.Y / Cell);
        int cx1 = (int)Math.Floor(b.Right / Cell), cy1 = (int)Math.Floor(b.Bottom / Cell);
        int total = 0, covered = 0;
        for (var cy = cy0; cy <= cy1; cy++)
        for (var cx = cx0; cx <= cx1; cx++)
        {
            if (!quad.Contains((cx + 0.5) * Cell, (cy + 0.5) * Cell)) continue;
            total++;
            var key = Key(cx, cy);
            if (mark) _covered.Add(key);
            else if (_covered.Contains(key)) covered++;
        }
        return (total, covered);
    }
}
