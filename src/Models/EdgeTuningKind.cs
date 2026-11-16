namespace HandPegApp.Models;

/// <summary>Which clip edge a control shapes while it is held, for the player's 400 % edge loupe.</summary>
public enum EdgeTuningKind
{
    None,

    /// <summary>A soft edge: the layer's feather, a corner feather, a mask's blur.</summary>
    Feather,

    /// <summary>A mask grown, shrunk or moved.</summary>
    Mask,

    /// <summary>A matte choked (eroded or dilated) or smoothed.</summary>
    Matte,

    /// <summary>A chroma key's colour, tolerance, softness or spill.</summary>
    ChromaKey,
}
