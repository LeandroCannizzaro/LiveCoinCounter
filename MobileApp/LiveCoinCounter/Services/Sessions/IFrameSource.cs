using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>Produttore di fotogrammi della fotocamera: CameraX (metodo OpenCV) o ARCore (metodi ARCore e Ibrido).</summary>
public interface IFrameSource
{
    string Id { get; }

    /// <param name="wantsFrame">Chiesto prima di convertire un fotogramma: se il motore è occupato lo si scarta subito.</param>
    Task StartAsync(Action<CameraFrame> onFrame, Func<bool> wantsFrame);

    Task StopAsync();

    /// <summary>La fotocamera ha la torcia e la si può accendere da qui.</summary>
    bool SupportsTorch => false;

    void SetTorch(bool on) { }

    /// <summary>Chiede di rimettere a fuoco al centro dell'inquadratura (quando le immagini risultano sfocate).</summary>
    void Refocus() { }
}
