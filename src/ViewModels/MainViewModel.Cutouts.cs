using System.IO;
using System.Text.Json;
using System.Windows;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// One named piece the Cutout Studio makes (a hero's portrait and health, the abilities, the killfeed): its outlines as a mask's text
/// (fractions of the source's frame, see <see cref="MaskStore"/>), and where it lies on a tall frame. Its own mask, so that what another
/// piece has inside this one's box never shows through it.
/// </summary>
/// <param name="Source">The box round its outlines, as fractions of the source.</param>
/// <param name="X">Its left edge on the frame, as a fraction of the frame's width.</param>
/// <param name="Y">Its top edge, as a fraction of the frame's height.</param>
/// <param name="Width">Its width, as a fraction of the frame's width.</param>
public sealed record CutoutPiece(string Name, string Shapes, Rect Source, double X, double Y, double Width);

// The Cutout Studio's way into the project and into a Game Preset (5.1-CleanupStudioAndFfmpegPurge): HUDs are drawn by hand, or will
// come from an ML model (v6.0); never guessed at by heuristics.
public partial class MainViewModel
{
    /// <summary>The zoom of the main video a Game Preset starts with: what its edges lose is what the pieces are for.</summary>
    public const double CutoutDefaultZoom = GamePresetLibrary.DefaultZoom;

    // The blurred background a vertical Game Preset starts with: the built-ins' (one source).
    private const double CutoutBlurRadius = GamePresetLibrary.BlurRadius, CutoutBackgroundDim = GamePresetLibrary.BackgroundDim;
    private const int CutoutBlurPasses = GamePresetLibrary.BlurPasses;

    /// <summary>The pieces the Studio made earlier in this project, which it opens with and takes the place of when added again.</summary>
    public IEnumerable<Layer> CutoutLayers => Layers.Where(l => l is { IsVideo: true, IsDeleted: false, UsesFrameMask: true, CustomMask: true } && l.MaskShapes.Length > 0 && ClipOf(l) is not null);

    /// <summary>
    /// The pieces as layers: each with a mask of its own, its outlines alone (filled in memory where it is drawn, no picture file:
    /// 5.1-Release), sharp, cut to its box. A piece with no outlines is left out; null when none has any.
    /// </summary>
    private static List<LayerState>? CutoutStates(IReadOnlyList<CutoutPiece> pieces)
    {
        var states = pieces.Where(piece => MaskStore.DrawnKey(piece.Shapes).Length > 0).Select(piece => new LayerState
        {
            Name = piece.Name,
            Kind = LayerKind.Video,
            SourceX = piece.Source.X, SourceY = piece.Source.Y, SourceWidth = piece.Source.Width, SourceHeight = piece.Source.Height,
            PositionX = piece.X, PositionY = piece.Y, SizeWidth = piece.Width,
            CustomMask = true, MaskPath = "", MaskFrame = true, MaskShapes = piece.Shapes, Feather = true, FeatherRadius = 1,
        }).ToList();
        return states.Count > 0 ? states : null;
    }

    /// <summary>
    /// The Studio's pieces on the timeline: a tall frame, the main video zoomed across its middle with its blurred background, and each
    /// piece a layer over it, a child of the main video. Pieces the Studio made before are replaced (into the recycle bin). One step of Undo.
    /// </summary>
    public void AddCutoutPieces(IReadOnlyList<CutoutPiece> pieces, double zoom)
    {
        if (pieces.Count == 0 || CutoutStates(pieces) is not { Count: > 0 } states)
            return;

        FlushLooks();
        var before = CaptureSnapshot();
        using var whole = DeferCommand();
        var old = CutoutLayers.ToList();
        if (old.Count > 0)
        {
            var binned = old.Select(v => (View: v, Deleted: Binned(ClipOf(v)!, Document.TrackOf(ClipOf(v)!)!, v))).ToList();
            if (Edit("take away the last cutout's layers", edit =>
                {
                    foreach (var (view, _) in binned)
                        edit.DeleteClip(ClipOf(view)!);
                }, undoable: false))
            {
                foreach (var (view, deleted) in binned)
                    AddToBin(deleted.Clip, view.Name, deleted.Preview);
            }
        }

        if (!UseVerticalResolution)
            UseVerticalResolution = true;

        PlacePieces(states, "add cutout pieces", mainInFront: false);
        PlaceEveryMainClip(Math.Clamp(zoom, SmallestCenterZoom, LargestCenterZoom), 0, 0);
        BlurRadius = CutoutBlurRadius;
        BlurPasses = CutoutBlurPasses;
        BackgroundDim = CutoutBackgroundDim;
        _backgroundRow.IsHidden = false;
        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        FrameEngine = true;
        if (!_isBackgroundWorker && HasSource)
            LivePreview = true;
        Checkpoint("add cutout pieces", before);
        StatusText = $"Added {states.Count} cutout piece{(states.Count == 1 ? "" : "s")} as layers. Ctrl+Z takes them away again.";
    }

    /// <summary>
    /// The Studio's pieces saved as a Game Preset: a .hpstyle in the Styles folder (one file: each piece's outlines are its mask) with the tall frame,
    /// the zoomed video and its blurred background, put first on the drop overlay and shown in the Style Presets list. An
    /// existing preset of that name is replaced. Nothing on the timeline changes.
    /// </summary>
    /// <returns>Null, or why it could not be saved.</returns>
    public string? SaveCutoutPreset(string name, IReadOnlyList<CutoutPiece> pieces, double zoom)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0)
            return "Type a name for the Game Preset first.";
        if (pieces.Count == 0)
            return "Draw at least one piece first.";
        if (CutoutStates(pieces) is not { Count: > 0 } states)
            return "The masks could not be drawn.";

        var preset = new StylePreset
        {
            Layout = new LayoutSection
            {
                CenterZoom = Math.Clamp(zoom, SmallestCenterZoom, LargestCenterZoom),
                SourceAspectRatio = CurrentSourceAspect,
                Vertical = true,
                MainVideoIndex = 0,
                Layers = states,
            },
            Blur = new BlurSection { Radius = CutoutBlurRadius, Passes = CutoutBlurPasses, Dim = CutoutBackgroundDim, Zoom = 1 },
        };
        EmbedMasks(preset);

        var file = safe + StyleFile.Extension;
        try
        {
            Directory.CreateDirectory(AppPaths.Styles);
            File.WriteAllText(Path.Combine(AppPaths.Styles, file), JsonSerializer.Serialize(preset, LayoutJsonOptions));

            // Asked to be on the overlay, it is: first of what it offers, and the overlay is made to offer at least one.
            var settings = AppSettings.Current;
            settings.SplashStylePresets = [file, .. settings.SplashStylePresets.Where(s => !string.Equals(s, file, StringComparison.OrdinalIgnoreCase))];
            settings.SplashPresetCount = Math.Max(settings.SplashPresetCount, 1);
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The Game Preset could not be saved: {ex.Message}";
        }

        RefreshStylePresets();
        StatusText = $"Game Preset \"{safe}\" saved: drop a video on it, or pick it from the Style Presets.";
        return null;
    }
}
