using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Diagnostics;

/// <summary>
/// Registro diagnostico di una sessione: eventi con un tipo e dati liberi, scritti a righe JSON (un evento per riga) in
/// un file che si può portare sul PC (adb) e analizzare. Non deve mai rallentare né far fallire l'acquisizione.
/// </summary>
public interface IDiagLog
{
    bool Enabled { get; }

    /// <param name="important">Gli eventi importanti si scrivono anche quando il file ha raggiunto la dimensione massima.</param>
    void Event(string type, object? data = null, bool important = false);
}

public sealed class NullDiagLog : IDiagLog
{
    public static readonly NullDiagLog Instance = new();
    public bool Enabled => false;
    public void Event(string type, object? data = null, bool important = false) { }
}

/// <summary>
/// Scrive su file in un thread dedicato: chi registra serializza e mette in coda (pochi microsecondi). Se la coda si riempie
/// gli eventi si scartano e si conta quanti (evento "log_dropped"). Oltre <see cref="MaxBytes"/> restano solo gli eventi importanti.
/// </summary>
public sealed class JsonlDiagLog : IDiagLog, IAsyncDisposable
{
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Envelope(long T, string E, object? D);

    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16384) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _writer;
    private readonly long _maxBytes;
    private long _bytes;
    private long _dropped;
    private int _truncatedLogged;

    public JsonlDiagLog(string path, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        _maxBytes = maxBytes;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = Task.Run(() => WriteAsync(path));
    }

    public string Path { get; }
    public bool Enabled => true;
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Millisecondi dall'apertura del registro (stesso orologio degli eventi).</summary>
    public long ElapsedMs => _clock.ElapsedMilliseconds;

    public void Event(string type, object? data = null, bool important = false)
    {
        try
        {
            if (!important && Interlocked.Read(ref _bytes) > _maxBytes)
            {
                if (Interlocked.Exchange(ref _truncatedLogged, 1) == 0) Event("log_truncated", new { maxBytes = _maxBytes }, important: true);
                return;
            }

            var line = JsonSerializer.Serialize(new Envelope(_clock.ElapsedMilliseconds, type, data), Options);
            Interlocked.Add(ref _bytes, line.Length + 1);
            if (!_lines.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // il registro non deve mai interferire con l'acquisizione
        }
    }

    private async Task WriteAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream);
            var lastFlush = Stopwatch.StartNew();
            var reportedDrops = 0L;
            await foreach (var line in _lines.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(line);
                var dropped = Interlocked.Read(ref _dropped);
                if (dropped != reportedDrops)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new Envelope(_clock.ElapsedMilliseconds, "log_dropped", new { total = dropped }), Options));
                    reportedDrops = dropped;
                }
                if (lastFlush.ElapsedMilliseconds > 1000 || _lines.Reader.Count == 0)
                {
                    await writer.FlushAsync();
                    lastFlush.Restart();
                }
            }
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Registro diagnostico interrotto: {ex.Message}");
            _lines.Writer.TryComplete();
        }
    }

    /// <summary>Scrive gli eventi rimasti in coda e chiude il file.</summary>
    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        await _writer;
    }
}

/// <summary>Conversioni in numeri semplici per i dati dei registri (compatti e leggibili).</summary>
public static class DiagData
{
    public static double R(double v, int digits = 1) => double.IsFinite(v) ? Math.Round(v, digits) : 0;

    public static double[] Quad(Quad q) =>
        new[] { R(q.A.X), R(q.A.Y), R(q.B.X), R(q.B.Y), R(q.C.X), R(q.C.Y), R(q.D.X), R(q.D.Y) };

    public static double[] Box(RectF b) => new[] { R(b.X), R(b.Y), R(b.Width), R(b.Height) };

    public static double[] Homography(Homography h) => h.ToArray().Select(v => R(v, 6)).ToArray();
}
