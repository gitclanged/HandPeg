using System.Windows;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>The ready-made shapes a layer can be cut to, stretched over the layer: see <see cref="HudLibrary.Outline"/>.</summary>
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
/// The shapes layers are cut to, and the vertical layout of pieces cut from the video. A game's HUD is a Game Preset: a built-in one
/// (<see cref="GamePresetLibrary"/>) or the user's own, drawn in the Cutout Studio (MaskDrawWindow) and saved as a style; an ML cutout
/// generator is bookmarked for v6.0. HandPeg never deletes a style.
/// </summary>
public static class HudLibrary
{
    /// <summary>
    /// Places the pieces on a 9:16 frame around a 16:9 video that fills its width at <paramref name="zoom"/>: one row above the
    /// video and one below, each as large as the row's width and the room above or below allow, in the left-to-right order
    /// the pieces have on screen.
    /// </summary>
    public static void LayOutVertically(List<LayerState> states, double zoom = 1)
    {
        // The share of the frame's height a video that fills its width takes; a piece's height is its width times this, per unit of its own shape.
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
    /// A ready shape's outline (5.2-Audit, the user's call: no picture is written to the Masks folder), as a drawn mask's text
    /// (<see cref="MaskStore.FormatShapes"/>) on the layer's own picture, which it is stretched over: the shape a little in
    /// from the edges, so the layer's Soft Edges (which blurs a mask) fades it out all the way round. Null for a plain panel,
    /// which needs no mask: its rounded corners are the layer's own.
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

    /// <summary>How soft a ready shape's edge is when the layer's own Soft Edges is off: as soft as the shapes' pictures were before 5.2-Audit.</summary>
    public const int ShapeFeatherRadius = 24;
}
