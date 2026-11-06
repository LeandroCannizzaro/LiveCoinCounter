using Android.Content;
using Android.Graphics;
using Android.Views;
using LiveCoinCounter.Controls;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Sessions;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using APaint = Android.Graphics.Paint;
using AView = Android.Views.View;

namespace LiveCoinCounter.Platforms.Android.Controls;

public sealed class MosaicViewHandler : ViewHandler<MosaicView, MosaicPlatformView>
{
    public static readonly IPropertyMapper<MosaicView, MosaicViewHandler> Mapper = new PropertyMapper<MosaicView, MosaicViewHandler>(ViewMapper);

    public static readonly CommandMapper<MosaicView, MosaicViewHandler> Commands = new(ViewCommandMapper)
    {
        [nameof(MosaicView.Refresh)] = (h, v, arg) => h.PlatformView.Refresh(v, arg as IReadOnlyList<TileKey>),
        [nameof(MosaicView.ResetZoom)] = (h, _, _) => h.PlatformView.ResetZoom(),
        [nameof(MosaicView.Clear)] = (h, _, _) => h.PlatformView.ClearAll()
    };

    public MosaicViewHandler() : base(Mapper, Commands) { }

    protected override MosaicPlatformView CreatePlatformView() => new(Context);

    protected override void DisconnectHandler(MosaicPlatformView platformView)
    {
        platformView.ClearAll();
        base.DisconnectHandler(platformView);
    }
}

/// <summary>Disegno nativo del mosaico: una Bitmap per tessera, aggiornata solo quando la tessera cambia.</summary>
public sealed class MosaicPlatformView : AView
{
    private readonly Dictionary<TileKey, (int Version, Bitmap Bitmap)> _tiles = new();
    private readonly ScaleGestureDetector _scale;
    private readonly GestureDetector _gestures;
    private readonly APaint _tilePaint = new() { FilterBitmap = true };
    private readonly APaint _boxPaint = new() { AntiAlias = true };
    private readonly APaint _labelPaint = new() { AntiAlias = true, FakeBoldText = true };
    private readonly APaint _labelBack = new() { AntiAlias = true };
    private readonly APaint _ghostPaint = new() { AntiAlias = true };
    private readonly APaint _quadPaint = new() { AntiAlias = true, Color = AColor.White };
    private readonly APaint _textPaint = new() { AntiAlias = true, Color = AColor.Argb(200, 255, 255, 255), TextAlign = APaint.Align.Center };
    private IReadOnlyList<OverlayBox> _boxes = Array.Empty<OverlayBox>();
    private bool _solid;
    private int _styleKey;
    private float _gridPxPerCm;
    private int _gridCm = 1;
    private string _gridLabel = string.Empty;
    private readonly APaint _gridPaint = new() { AntiAlias = true };
    private Quad? _quad;
    private Quad? _ghost;
    private Mosaic.RectF _bounds;
    private Bitmap? _preview;
    private int _previewRotation;
    private string _emptyText = string.Empty;

    // zoom dell'utente sopra l'adattamento automatico
    private float _zoom = 1;
    private float _panX, _panY;

    public MosaicPlatformView(Context context) : base(context)
    {
        _boxPaint.SetStyle(APaint.Style.Stroke);
        _quadPaint.SetStyle(APaint.Style.Stroke);
        _quadPaint.SetPathEffect(new DashPathEffect(new[] { 12f, 8f }, 0));
        _textPaint.TextSize = 16 * Resources!.DisplayMetrics!.Density;
        _scale = new ScaleGestureDetector(context, new ScaleListener(this));
        _gestures = new GestureDetector(context, new GestureListener(this));
    }

    public void Refresh(MosaicView view, IReadOnlyList<TileKey>? changed)
    {
        _boxes = view.Boxes;
        _quad = view.FrameQuad;
        _ghost = view.GhostQuad;
        _emptyText = view.EmptyText;
        _gridPxPerCm = view.GridPxPerCm;
        _gridCm = Math.Max(1, view.GridCm);
        _gridLabel = view.GridLabel;
        var canvas = view.Source;
        if (canvas is not null)
        {
            // il piano di colore dipende anche dalle monete: se cambiano (o cambia lo stile) si ridisegnano tutte le tessere
            var solid = view.SolidSurface;
            var style = solid ? HashCode.Combine(view.SurfaceColor, StyleKey(_boxes)) : 0;
            var force = solid != _solid || style != _styleKey;
            _solid = solid;
            _styleKey = style;

            var keys = force ? canvas.Keys : changed ?? canvas.Keys;
            var snapshot = solid ? canvas.SnapshotSurface(keys, view.SurfaceColor, _boxes.Select(b => b.Box).ToList()) : canvas.Snapshot(keys);
            foreach (var (key, version, pixels) in snapshot)
            {
                _tiles.TryGetValue(key, out var existing);
                if (!force && existing.Bitmap is not null && existing.Version == version) continue;
                var bitmap = existing.Bitmap ?? Bitmap.CreateBitmap(MosaicCanvas.TileSize, MosaicCanvas.TileSize, Bitmap.Config.Argb8888!)!;
                bitmap.SetPixels(pixels, 0, MosaicCanvas.TileSize, 0, 0, MosaicCanvas.TileSize, MosaicCanvas.TileSize);
                _tiles[key] = (version, bitmap);
            }
            _bounds = canvas.Bounds;
        }

        if (view.Preview is { } preview)
        {
            _preview?.Dispose();
            _preview = AndroidImaging.ToBitmap(preview);
            _previewRotation = view.PreviewRotation;
        }
        else if (_preview is not null)
        {
            _preview.Dispose();
            _preview = null;
        }
        Invalidate();
    }

    /// <summary>Impronta delle monete (posizione e misura in pixel interi): cambia solo quando cambia davvero qualcosa da ridisegnare.</summary>
    private static int StyleKey(IReadOnlyList<OverlayBox> boxes)
    {
        var hash = new HashCode();
        foreach (var b in boxes)
        {
            hash.Add((int)Math.Round(b.Box.X));
            hash.Add((int)Math.Round(b.Box.Y));
            hash.Add((int)Math.Round(b.Box.Width));
        }
        return hash.ToHashCode();
    }

    public void ResetZoom()
    {
        _zoom = 1;
        _panX = _panY = 0;
        Invalidate();
    }

    public void ClearAll()
    {
        foreach (var (_, bitmap) in _tiles.Values) bitmap.Dispose();
        _tiles.Clear();
        _preview?.Dispose();
        _preview = null;
        _boxes = Array.Empty<OverlayBox>();
        _quad = null;
        _ghost = null;
        _bounds = default;
        ResetZoom();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        _scale.OnTouchEvent(e);
        _gestures.OnTouchEvent(e);
        return true;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawColor(AColor.Rgb(18, 18, 20));
        var density = Resources!.DisplayMetrics!.Density;

        if (_tiles.Count == 0 || _bounds.Width <= 0)
        {
            if (!string.IsNullOrEmpty(_emptyText))
                canvas.DrawText(_emptyText, Width / 2f, Height / 2f, _textPaint);
            DrawPreview(canvas, density);
            return;
        }

        // adattamento: tutto il mosaico (e la zona inquadrata) nella vista, con un margine
        var content = _bounds;
        if (_quad is { } q)
        {
            var qb = q.Bounds;
            content = Mosaic.RectF.FromBounds(Math.Min(content.X, qb.X), Math.Min(content.Y, qb.Y), Math.Max(content.Right, qb.Right), Math.Max(content.Bottom, qb.Bottom));
        }
        if (_ghost is { } g)
        {
            var gb = g.Bounds;
            content = Mosaic.RectF.FromBounds(Math.Min(content.X, gb.X), Math.Min(content.Y, gb.Y), Math.Max(content.Right, gb.Right), Math.Max(content.Bottom, gb.Bottom));
        }
        var padding = 24 * density;
        var fit = Math.Min((Width - 2 * padding) / content.Width, (Height - 2 * padding) / content.Height);
        fit = Math.Min(fit, 4f);
        var scale = fit * _zoom;

        canvas.Save();
        canvas.Translate(Width / 2f + _panX, Height / 2f + _panY);
        canvas.Scale(scale, scale);
        canvas.Translate(-content.CenterX, -content.CenterY);

        // ogni tessera sborda di mezzo pixel: senza, il filtro bilineare lascia una riga scura sulle giunture
        foreach (var (key, (_, bitmap)) in _tiles)
        {
            float x = key.X * MosaicCanvas.TileSize, y = key.Y * MosaicCanvas.TileSize;
            canvas.DrawBitmap(bitmap, null, new global::Android.Graphics.RectF(x - 0.5f, y - 0.5f, x + MosaicCanvas.TileSize + 0.5f, y + MosaicCanvas.TileSize + 0.5f), _tilePaint);
        }

        DrawGrid(canvas, content, density, scale);

        _boxPaint.StrokeWidth = 2.5f * density / scale;
        _labelPaint.TextSize = 12 * density / scale;
        foreach (var box in _boxes)
        {
            var color = box.Highlight ? AColor.Rgb(33, 150, 243) : box.Stable ? AColor.Rgb(46, 204, 113) : AColor.Rgb(190, 190, 190);
            _boxPaint.Color = color;
            var b = box.Box;
            canvas.DrawRect(b.X, b.Y, b.Right, b.Bottom, _boxPaint);
            if (box.Label is { Length: > 0 } label)
            {
                var w = _labelPaint.MeasureText(label) + 6 * density / scale;
                _labelBack.Color = color;
                canvas.DrawRect(b.X, b.Y - _labelPaint.TextSize - 4 * density / scale, b.X + w, b.Y, _labelBack);
                _labelPaint.Color = AColor.Black;
                canvas.DrawText(label, b.X + 3 * density / scale, b.Y - 3 * density / scale, _labelPaint);
            }
        }

        if (_quad is { } quad)
        {
            _quadPaint.StrokeWidth = 2 * density / scale;
            using var path = QuadPath(quad);
            canvas.DrawPath(path, _quadPaint);
        }

        // posizione persa: l'ultima zona nota, in arancione, dove l'operatore deve tornare
        if (_ghost is { } ghost)
        {
            using var path = QuadPath(ghost);
            _ghostPaint.SetStyle(APaint.Style.Fill);
            _ghostPaint.Color = AColor.Argb(70, 255, 152, 0);
            canvas.DrawPath(path, _ghostPaint);
            _ghostPaint.SetStyle(APaint.Style.Stroke);
            _ghostPaint.StrokeWidth = 4 * density / scale;
            _ghostPaint.Color = AColor.Rgb(255, 152, 0);
            canvas.DrawPath(path, _ghostPaint);

            var text = "Torna qui";
            _labelPaint.TextSize = 18 * density / scale;
            var w = _labelPaint.MeasureText(text);
            float cx = (float)ghost.Bounds.CenterX, cy = (float)ghost.Bounds.CenterY;
            _labelBack.Color = AColor.Rgb(255, 152, 0);
            canvas.DrawRect(cx - w / 2 - 6 * density / scale, cy - _labelPaint.TextSize, cx + w / 2 + 6 * density / scale, cy + 8 * density / scale, _labelBack);
            _labelPaint.Color = AColor.Black;
            canvas.DrawText(text, cx - w / 2, cy, _labelPaint);
        }
        canvas.Restore();

        // legenda della griglia (fuori dal mosaico, sempre leggibile)
        if (_gridLabel.Length > 0)
        {
            _textPaint.TextAlign = APaint.Align.Left;
            _textPaint.TextSize = 12 * density;
            canvas.DrawText(_gridLabel, 16 * density, Height - 150 * density, _textPaint);
            _textPaint.TextAlign = APaint.Align.Center;
            _textPaint.TextSize = 16 * density;
        }

        DrawPreview(canvas, density);
    }

    /// <summary>
    /// Griglia metrica sul mosaico, ancorata all'origine del mosaico (non si sposta quando la superficie cresce): una riga ogni
    /// <see cref="_gridCm"/> cm, più marcata ogni 5 righe se il passo è 1 cm o 2 cm (5 e 10 cm), o ogni 2 se è 5 cm.
    /// </summary>
    private void DrawGrid(Canvas canvas, Mosaic.RectF content, float density, float scale)
    {
        if (_gridPxPerCm <= 0) return;
        var step = _gridPxPerCm * _gridCm;
        // sotto i 6 px sullo schermo la griglia diventa una macchia: si salta una riga su due finché non torna leggibile
        var skip = 1;
        while (step * skip * scale < 6 * density && skip < 64) skip *= 2;
        step *= skip;
        var major = _gridCm switch { 1 => 5, 2 => 5, 5 => 2, _ => 1 };

        _gridPaint.SetStyle(APaint.Style.Stroke);
        var firstX = (int)Math.Floor(content.X / step);
        var lastX = (int)Math.Ceiling(content.Right / step);
        var firstY = (int)Math.Floor(content.Y / step);
        var lastY = (int)Math.Ceiling(content.Bottom / step);
        for (var i = firstX; i <= lastX; i++)
        {
            var isMajor = i % major == 0;
            _gridPaint.Color = AColor.Argb(isMajor ? 150 : 70, 255, 255, 255);
            _gridPaint.StrokeWidth = (isMajor ? 1.4f : 0.7f) * density / scale;
            canvas.DrawLine(i * step, (float)content.Y, i * step, (float)content.Bottom, _gridPaint);
        }
        for (var j = firstY; j <= lastY; j++)
        {
            var isMajor = j % major == 0;
            _gridPaint.Color = AColor.Argb(isMajor ? 150 : 70, 255, 255, 255);
            _gridPaint.StrokeWidth = (isMajor ? 1.4f : 0.7f) * density / scale;
            canvas.DrawLine((float)content.X, j * step, (float)content.Right, j * step, _gridPaint);
        }
    }

    private static global::Android.Graphics.Path QuadPath(Quad quad)
    {
        var path = new global::Android.Graphics.Path();
        path.MoveTo((float)quad.A.X, (float)quad.A.Y);
        path.LineTo((float)quad.B.X, (float)quad.B.Y);
        path.LineTo((float)quad.C.X, (float)quad.C.Y);
        path.LineTo((float)quad.D.X, (float)quad.D.Y);
        path.Close();
        return path;
    }

    /// <summary>Anteprima della fotocamera in alto a destra, raddrizzata.</summary>
    private void DrawPreview(Canvas canvas, float density)
    {
        if (_preview is null) return;
        var rotated = _previewRotation is 90 or 270;
        var srcW = rotated ? _preview.Height : _preview.Width;
        var srcH = rotated ? _preview.Width : _preview.Height;
        var targetW = Width * 0.3f;
        var s = targetW / srcW;
        var targetH = srcH * s;
        var margin = 12 * density;
        var left = Width - targetW - margin;
        var top = margin + 56 * density;

        canvas.Save();
        canvas.Translate(left + targetW / 2, top + targetH / 2);
        canvas.Rotate(_previewRotation);
        canvas.Scale(s, s);
        canvas.DrawBitmap(_preview, -_preview.Width / 2f, -_preview.Height / 2f, _tilePaint);
        canvas.Restore();

        _boxPaint.Color = AColor.White;
        _boxPaint.StrokeWidth = 1.5f * density;
        canvas.DrawRect(left, top, left + targetW, top + targetH, _boxPaint);
    }

    private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly MosaicPlatformView _view;
        public ScaleListener(MosaicPlatformView view) => _view = view;

        public override bool OnScale(ScaleGestureDetector detector)
        {
            _view._zoom = Math.Clamp(_view._zoom * detector.ScaleFactor, 0.5f, 12f);
            _view.Invalidate();
            return true;
        }
    }

    private sealed class GestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly MosaicPlatformView _view;
        public GestureListener(MosaicPlatformView view) => _view = view;

        public override bool OnDown(MotionEvent e) => true;

        public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
        {
            _view._panX -= distanceX;
            _view._panY -= distanceY;
            _view.Invalidate();
            return true;
        }

        public override bool OnDoubleTap(MotionEvent e)
        {
            _view.ResetZoom();
            return true;
        }
    }
}
