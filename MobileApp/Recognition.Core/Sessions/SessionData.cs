using SQLite;

namespace LiveCoinCounter.Sessions;

public enum SessionStatus { Acquiring, Classifying, Completed, Aborted }

public enum SessionSource { Live, Photo }

public enum CoinState { Detected, Classified, Corrected, Rejected }

/// <summary>Cosa resta su disco a fine sessione (impostazione "Archivio delle sessioni").</summary>
public enum RetentionMode
{
    /// <summary>Mosaico ridotto e un ritaglio per moneta (predefinito).</summary>
    MosaicAndCrops,
    /// <summary>In più le foto originali usate per il mosaico.</summary>
    WithKeyframes,
    /// <summary>Come il predefinito; con le opzioni da sviluppatore registra anche tutti i fotogrammi.</summary>
    DebugFrames
}

/// <summary>Uso dell'AI in rete dopo lo stop (impostazione "Riconoscimento dopo lo stop").</summary>
public enum AiMode
{
    /// <summary>Sempre sul telefono; "Riprova con l'AI le incerte" su richiesta (predefinito).</summary>
    OfflineAiOnRequest,
    /// <summary>Nessun dato esce dal telefono.</summary>
    OfflineOnly,
    /// <summary>Le monete incerte vanno subito all'AI se c'è rete.</summary>
    AiAutoUncertain
}

[Table("Session")]
public sealed class SessionRecord
{
    [PrimaryKey] public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; }
    public SessionSource Source { get; set; }
    public string TrackerId { get; set; } = string.Empty;
    public RetentionMode Retention { get; set; }
    public int MosaicWidth { get; set; }
    public int MosaicHeight { get; set; }
    public string? MosaicPreviewPath { get; set; }

    /// <summary>Scala dell'anteprima: pixel dell'anteprima per pixel del mosaico, e origine del mosaico nell'anteprima.</summary>
    public float PreviewScale { get; set; } = 1;
    public double PreviewOriginX { get; set; }
    public double PreviewOriginY { get; set; }

    /// <summary>Pixel del mosaico per millimetro, se nota (ARCore).</summary>
    public double? PxPerMm { get; set; }
    public int KeyframeCount { get; set; }
    public string LocatorId { get; set; } = string.Empty;
    public string? ClassifierId { get; set; }
    public int TotalCents { get; set; }
    public int CoinCount { get; set; }
    public int UncertainCount { get; set; }
    public string? Notes { get; set; }

    /// <summary>Quando l'utente ha verificato la sessione (conferme, correzioni, monete aggiunte): da qui in poi i dati valgono come "verità".</summary>
    public DateTime? ReviewedAt { get; set; }
}

/// <summary>Esito della verifica manuale di una moneta (<see cref="CoinRecord.TruthKind"/>).</summary>
public static class TruthKinds
{
    /// <summary>Il riconoscimento era giusto.</summary>
    public const string Ok = "ok";
    /// <summary>Moneta vera ma valore sbagliato.</summary>
    public const string WrongValue = "wrong_value";
    /// <summary>Non era una moneta (falso rilevamento).</summary>
    public const string NotCoin = "not_coin";
    /// <summary>La stessa moneta contata due volte.</summary>
    public const string Duplicate = "duplicate";
    /// <summary>Moneta non rilevata, aggiunta a mano.</summary>
    public const string Manual = "manual";
}

[Table("Keyframe")]
public sealed class KeyframeRecord
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public string SessionId { get; set; } = string.Empty;
    public int Index { get; set; }
    public DateTime CapturedAt { get; set; }
    /// <summary>Omografia fotogramma → mosaico (9 valori separati da virgola).</summary>
    public string Homography { get; set; } = string.Empty;
    public double Sharpness { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? ImagePath { get; set; }
}

[Table("Coin")]
public sealed class CoinRecord
{
    /// <summary>Chiave composta logica (sessione, numero): l'Id SQLite resta un intero autoincrementale.</summary>
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public string SessionId { get; set; } = string.Empty;
    /// <summary>Numero della moneta nella sessione (Id del <see cref="CoinTrack"/>).</summary>
    public int Number { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float DetectionConfidence { get; set; }
    public int Observations { get; set; }
    public int SourceKeyframe { get; set; }
    public string? CropPath { get; set; }
    public CoinState State { get; set; }
    public int? DetectorCents { get; set; }
    public float DetectorClassConfidence { get; set; }
    public int? Cents { get; set; }
    public float ClassConfidence { get; set; }
    public int? SecondCents { get; set; }
    public float SecondScore { get; set; }
    public string? ClassifierId { get; set; }
    public string? Note { get; set; }
    public int? CorrectedCents { get; set; }
    public DateTime DetectedAt { get; set; }

    /// <summary>L'utente ha controllato questa moneta (conferma o correzione): è "verità" per il debug.</summary>
    public bool Verified { get; set; }

    /// <summary>Vedi <see cref="TruthKinds"/>; null finché la moneta non è stata verificata.</summary>
    public string? TruthKind { get; set; }
    public string? TruthNote { get; set; }

    /// <summary>Moneta aggiunta a mano perché il rilevatore non l'ha trovata: non ha posizione né ritaglio.</summary>
    public bool IsManual { get; set; }

    /// <summary>Il valore che conta: la correzione dell'utente vince sempre; le monete scartate valgono zero.</summary>
    [Ignore] public int? EffectiveCents => State == CoinState.Rejected ? null : CorrectedCents ?? Cents;

    [Ignore] public bool IsUncertain => State == CoinState.Classified && ClassConfidence < SessionRepository.UncertainThreshold;

    [Ignore] public float Diameter => (Width + Height) / 2;
}

[Table("Classification")]
public sealed class ClassificationRecord
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int CoinId { get; set; }
    public string ClassifierId { get; set; } = string.Empty;
    public int Cents { get; set; }
    public float Confidence { get; set; }
    /// <summary>Tagli candidati "centesimi:punteggio" separati da punto e virgola.</summary>
    public string TopK { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

[Table("SchemaInfo")]
public sealed class SchemaInfo
{
    [PrimaryKey] public int Id { get; set; }
    public int Version { get; set; }
}
