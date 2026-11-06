using System.Text.Json;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Sessions;

/// <summary>I marker di scala di una sessione, salvati come JSON nel record della sessione.</summary>
public static class SessionMarkers
{
    private sealed record Item(int Id, double X, double Y, double PxPerMm, int Observations);

    public static string? Serialize(IReadOnlyList<MarkerLandmark> landmarks) =>
        landmarks.Count == 0 ? null : JsonSerializer.Serialize(landmarks.Select(l => new Item(l.Id, l.X, l.Y, l.PxPerMm, l.Observations)));

    public static IReadOnlyList<MarkerLandmark> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MarkerLandmark>();
        try
        {
            return JsonSerializer.Deserialize<List<Item>>(json)?.Select(i => new MarkerLandmark(i.Id, i.X, i.Y, i.PxPerMm, i.Observations)).ToList()
                   ?? (IReadOnlyList<MarkerLandmark>)Array.Empty<MarkerLandmark>();
        }
        catch (JsonException)
        {
            return Array.Empty<MarkerLandmark>();
        }
    }
}
