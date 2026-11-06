using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services;

/// <summary>Come si disegna la superficie acquisita di una sessione: foto o piano di colore, riquadri delle monete, griglia metrica.</summary>
/// <param name="View">Superficie continua, frame acquisiti o foto reale.</param>
/// <param name="Scale">La scala per la griglia; senza scala la griglia non si disegna.</param>
public sealed record SurfaceRender(SurfaceLook View = SurfaceLook.Continuous, bool Boxes = true, GridConfig? Grid = null, SurfaceScale? Scale = null);
