namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Ridimensionamento dell'immagine prima dell'invio all'AI. Dipende dalla piattaforma, quindi chi usa la libreria
/// lo imposta all'avvio (l'app con <c>Android.Graphics</c>, l'etichettatore con SkiaSharp). Di default non fa nulla.
/// </summary>
public static class ImageShrink
{
    public static Func<byte[], byte[]> Shrinker { get; set; } = bytes => bytes;
}
