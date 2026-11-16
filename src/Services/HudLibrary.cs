using System.Windows;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>Built-in layer mask shapes, stretched over the layer (<see cref="HudLibrary.Outline"/>).</summary>
public enum HudShape
{
    /// <summary>A plain rectangle with slightly rounded corners.</summary>
    Panel,

    /// <summary>A circle or oval: a round radar, an ultimate meter.</summary>
    Ellipse,

    /// <summary>A bar leaning to the right, as slanted health bars do.</summary>
    Slant,

    /// <summary>A rectangle whose edges fade out, so the piece sits on the picture without a hard border.</summary>
    Soft,
}

/// <summary>
/// Layer mask shapes and vertical layout of Cutout pieces. Game HUDs are Game Presets: built-in (<see cref="GamePresetLibrary"/>) or
/// custom, drawn in Cutout Studio (MaskDrawWindow) and saved as styles. Styles are never deleted automatically.
/// </summary>
public static class HudLibrary
{
    /// <summary>
    /// Lays out pieces on a 9:16 frame around a full-width 16:9 video at <paramref name="zoom"/>: one row above and one below, each
    /// scaled to fit its row, preserving on-screen left-to-right order.
    /// </summary>
    public static void LayOutVertically(List<LayerState> states, double zoom = 1)
    {
        // Frame-height fraction of a full-width video; a piece's height is width x this per unit of its aspect.
        const double band = 81.0 / 256, gap = 0.02, margin = 0.03, largest = 3.2;
        var room = (1 - band * Math.Max(zoom, 1)) / 2 - margin - 0.012;

        foreach (var above in new[] { true, false })
        {
            var row = states.Select(state => (Piece: (X: state.SourceX, Y: state.SourceY, Width: state.SourceWidth, Height: state.SourceHeight), State: state))
                .Where(p => (p.Piece.Y + p.Piece.Height / 2 < 0.5) == above).OrderBy(p => p.Piece.X).ToList();
            if (row.Count == 0)
                continue;

            var widths = row.Sum(p => p.Piece.Width);
            var scale = Math.Min(Math.Min((1 - 2 * margin - gap * (row.Count - 1)) / widths, room / (band * row.Max(p => p.Piece.Height))), largest);
            var x = (1 - (widths * scale + gap * (row.Count - 1))) / 2;
            var top = above ? margin : 1 - margin - room;
            foreach (var (piece, state) in row)
            {
                state.SizeWidth = Math.Round(piece.Width * scale, 4);
                state.PositionX = Math.Round(x, 4);
                state.PositionY = Math.Round(top + (room - piece.Height * scale * band) / 2, 4);
                x += piece.Width * scale + gap;
            }
        }
    }

    /// <summary>
    /// Built-in shape as drawn-mask text (<see cref="MaskStore.FormatShapes"/>, no bitmap written), stretched over the layer and inset so
    /// Soft Edges fades it on all sides. Null for a plain panel (uses the layer's own rounded corners).
    /// </summary>
    public static string? Outline(HudShape shape)
    {
        const double inset = 0.045, size = 1 - 2 * inset;
        List<Point>? outline = shape switch
        {
            HudShape.Ellipse => MaskStore.Ellipse(inset, inset, size, size, 64),
            HudShape.Slant => MaskStore.Slant(inset, inset, size, size, 0.1 / size),
            HudShape.Soft => MaskStore.RoundedRectangle(inset, inset, size, size, 24),
            _ => null,
        };
        return outline is null ? null : MaskStore.FormatShapes([outline]);
    }

    /// <summary>Default shape edge softness when the layer's Soft Edges is off (matches legacy bitmap shapes).</summary>
    public const int ShapeFeatherRadius = 24;
}
