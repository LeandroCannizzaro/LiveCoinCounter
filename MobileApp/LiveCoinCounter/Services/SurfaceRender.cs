using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services;

/// <summary>Come si disegna la superficie acquisita di una sessione: foto o piano di colore, riquadri delle monete, griglia metrica.</summary>
/// <param name="Photo">La foto reale del tavolo invece del piano di colore.</param>
/// <param name="Scale">La scala per la griglia; senza scala la griglia non si disegna.</param>
public sealed record SurfaceRender(bool Photo = false, bool Boxes = true, GridSpacing Grid = GridSpacing.Off, SurfaceScale? Scale = null);
