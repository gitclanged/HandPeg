using System.IO;
using System.Text.Json;
using System.Windows;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// Named Cutout Studio region (e.g. hero portrait and health, abilities, killfeed): outlines as mask text in source-frame fractions
/// (<see cref="MaskStore"/>) and its placement on a vertical frame. Each has its own mask so other Cutouts never bleed through.
/// </summary>
/// <param name="Source">Outline bounding box, source fractions.</param>
/// <param name="X">Left edge, frame-width fraction.</param>
/// <param name="Y">Top edge, frame-height fraction.</param>
/// <param name="Width">Width, frame-width fraction.</param>
public sealed record CutoutPiece(string Name, string Shapes, Rect Source, double X, double Y, double Width);

// Cutout Studio output to the project and to Game Presets. HUD regions are hand-drawn (or model-generated), never heuristic.
public partial class MainViewModel
{
    /// <summary>Initial Game Preset video width (frame-width fraction); Cutouts restore what the cropped edges lose.</summary>
    public const double CutoutDefaultZoom = GamePresetLibrary.DefaultZoom;

    // Default vertical Game Preset blur fill, shared with the built-ins.
    private const double CutoutBlurRadius = GamePresetLibrary.BlurRadius, CutoutBackgroundDim = GamePresetLibrary.BackgroundDim;
    private const int CutoutBlurPasses = GamePresetLibrary.BlurPasses;

    /// <summary>
    /// Existing Studio Cutouts in this project (same-file clips with drawn outlines); the Studio opens with them and replaces them on
    /// re-add.
    /// </summary>
    public IEnumerable<Layer> CutoutLayers => Layers.Where(l => l is { CustomMask: true } && l.MaskShapes.Length > 0 && IsCutout(l));

    // ----- The Cutout Studio's family -----

    /// <summary>
    /// The Cutout Studio's base clip for a pick (Timeline selection, clip menu): a Cutout climbs to the video it is cut from, any
    /// other video clip is its own base, and nothing picked takes <see cref="PlayheadVideo"/>. The heat lamp's still and the window
    /// both resolve through here, so a prewarmed still always matches.
    /// </summary>
    public Layer CutoutStudioBase(Layer? picked)
    {
        if (picked is { IsVideoFile: true, IsDeleted: false } && ClipOf(picked) is { } clip)
        {
            for (var hops = 0; hops < 32 && IsCutout(clip) && Document.FindClip(clip.ParentClipId!.Value) is { } parent; hops++)
                clip = parent;
            if (ViewOf(clip) is { } view)
                return view;
        }

        return PlayheadVideo;
    }

    /// <summary>
    /// The Cutouts cut from <paramref name="baseClip"/> (its same-file children) as Studio pieces: drawn outlines as they are, else
    /// the traced islands of a bitmap mask inside the clip's box (older Game Presets), else that box as a rectangle; placement from
    /// the clip. A Cutout showing the whole frame unmasked is left out. With <paramref name="orAnyClip"/>, a base without Cutouts
    /// takes those of the first top-level video clip that has some.
    /// </summary>
    public List<(Layer Clip, CutoutPiece Piece)> CutoutFamily(Layer baseClip, bool orAnyClip)
    {
        var traced = new Dictionary<string, List<List<Point>>?>(StringComparer.OrdinalIgnoreCase);
        var family = FamilyOf(baseClip, traced);
        if (family.Count > 0 || !orAnyClip)
            return family;

        foreach (var clip in Document.Tracks.SelectMany(t => t.Clips).Where(c => c is { ContentKind: LayerKind.VideoFile, ParentClipId: null }))
        {
            if (ViewOf(clip) is { } view && !ReferenceEquals(view, baseClip) && FamilyOf(view, traced) is { Count: > 0 } other)
                return other;
        }

        return family;
    }

    private List<(Layer Clip, CutoutPiece Piece)> FamilyOf(Layer baseClip, Dictionary<string, List<List<Point>>?> traced)
    {
        var family = new List<(Layer, CutoutPiece)>();
        if (ClipOf(baseClip) is not { } clip)
            return family;

        foreach (var (_, child) in TemplateChildrenOf(clip))
        {
            if (!IsCutout(child) || ViewOf(child) is not { IsDeleted: false } view || StudioOutlines(view, traced) is not { Count: > 0 } outlines)
                continue;

            var (left, top) = (outlines.Min(o => o.Min(p => p.X)), outlines.Min(o => o.Min(p => p.Y)));
            var (right, bottom) = (outlines.Max(o => o.Max(p => p.X)), outlines.Max(o => o.Max(p => p.Y)));
            var bounds = new Rect(left, top, Math.Max(right - left, 1e-4), Math.Max(bottom - top, 1e-4));
            family.Add((view, new CutoutPiece(view.Name, MaskStore.FormatShapes(outlines), bounds, view.PositionX, view.PositionY, view.SizeWidth)));
        }

        return family;
    }

    /// <summary>Whether <see cref="StudioOutlines"/> gives a Cutout outlines, without tracing its mask.</summary>
    private static bool IsStudioPiece(Layer cutout)
    {
        var (_, _, width, height) = cutout.CroppedSource;
        return (cutout.CustomMask && MaskStore.SourceOf(cutout).Length > 0) || (width > 1e-4 && height > 1e-4 && (width < 0.999 || height < 0.999));
    }

    /// <summary>A Cutout's outlines in frame fractions (see <see cref="CutoutFamily"/>); null when it shows the whole frame unmasked.</summary>
    private static List<List<Point>>? StudioOutlines(Layer cutout, Dictionary<string, List<List<Point>>?> traced)
    {
        var mask = MaskStore.SourceOf(cutout);
        if (cutout.CustomMask && MaskStore.IsDrawn(mask))
            return MaskStore.ParseShapes(cutout.MaskShapes);

        var (x, y, width, height) = cutout.CroppedSource;
        var box = new Rect(x, y, Math.Max(width, 0), Math.Max(height, 0));
        if (cutout.CustomMask && mask.Length > 0 && MaskStore.Exists(mask))
        {
            if (!traced.TryGetValue(mask, out var islands))
                traced[mask] = islands = MaskTrace.Outlines(mask);

            // A frame mask is drawn on the whole frame; a picture mask on the clip's source area (the whole frame for presets).
            var (sx, sy, sw, sh) = cutout.UsesFrameMask ? (0.0, 0.0, 1.0, 1.0) : (cutout.SourceX, cutout.SourceY, cutout.SourceWidth, cutout.SourceHeight);
            var inside = (islands ?? []).Select(o => o.Select(p => new Point(sx + p.X * sw, sy + p.Y * sh)).ToList())
                .Where(o => box.Contains(new Point((o.Min(p => p.X) + o.Max(p => p.X)) / 2, (o.Min(p => p.Y) + o.Max(p => p.Y)) / 2))).ToList();
            if (inside.Count > 0)
                return inside;
        }

        if (box.Width <= 1e-4 || box.Height <= 1e-4 || (box.Width >= 0.999 && box.Height >= 0.999))
            return null;

        return [[box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft]];
    }

    /// <summary>
    /// The Game Preset name a set of Cutouts came from: the shared "Name:" prefix ("Overwatch: Killfeed"), else the words every name
    /// starts with ("Overwatch Killfeed", "Overwatch Health"); null when they share none.
    /// </summary>
    public static string? PresetNameOf(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return null;

        var prefixes = names.Select(n => n.IndexOf(':') is > 0 and var colon ? n[..colon].Trim() : null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (prefixes is [{ Length: > 0 } shared])
            return shared;
        if (names.Count < 2)
            return null;

        var words = names.Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var common = words[0].TakeWhile((word, i) => words.All(w => w.Length > i + 1 && string.Equals(w[i], word, StringComparison.OrdinalIgnoreCase))).ToList();
        return common.Count > 0 ? string.Join(' ', common) : null;
    }

    /// <summary>
    /// Cutouts as clip looks: each with its own outline-only mask (rasterized in memory, no image file), hard-edged and cropped to its
    /// box. Outline-less Cutouts are skipped; null when none remain.
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
    /// Applies Studio Cutouts to the timeline: vertical frame; each target video clip (selected, else all top-level) centred at the
    /// zoom width with blur fill; each Cutout a child clip above it on its clock. Prior Studio Cutouts on those clips go to the
    /// recycle bin. One undo step.
    /// </summary>
    public void AddCutouts(IReadOnlyList<CutoutPiece> pieces, double zoom)
    {
        if (pieces.Count == 0 || CutoutStates(pieces) is not { Count: > 0 } states || CutoutTargets("Cutouts") is not { Count: > 0 } targets)
            return;

        FlushLooks();
        var before = CaptureSnapshot();
        using var held = HoldLookSteps();
        using var whole = DeferCommand();
        // Every Cutout the Studio opened with as a piece (drawn, bitmap-masked or boxed), so re-adding never stacks a second set.
        var old = Layers.Where(l => IsCutout(l) && IsCutoutOf(l, targets) && IsStudioPiece(l)).ToList();
        if (old.Count > 0)
        {
            var binned = old.Select(v => (View: v, Deleted: Binned(ClipOf(v)!, Document.TrackOf(ClipOf(v)!)!, v))).ToList();
            if (Edit("take away the last Cutouts", edit =>
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

        var template = TemplateOf(states, videoIndex: 0, look: null, zoom, 0, 0, blurFilled: true);
        foreach (var id in targets)
            PutTemplateOn(id, template, "add Cutouts");
        BlurRadius = CutoutBlurRadius;
        BlurPasses = CutoutBlurPasses;
        BackgroundDim = CutoutBackgroundDim;
        _backgroundRow.IsHidden = false;
        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        Checkpoint("add Cutouts", before);
        StatusText = $"Added {states.Count} Cutout{(states.Count == 1 ? "" : "s")}. Ctrl+Z takes them away again.";
    }

    /// <summary>
    /// Saves Studio Cutouts as a Game Preset (.hpstyle in Styles; single file with outline masks) with vertical frame, zoomed video and
    /// blur fill, listed first on the drop overlay and in Game Presets. Overwrites a same-named preset; timeline unchanged.
    /// </summary>
    /// <returns>Null on success, else the error.</returns>
    public string? SaveCutoutPreset(string name, IReadOnlyList<CutoutPiece> pieces, double zoom)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0)
            return "Type a name for the Game Preset first.";
        if (pieces.Count == 0)
            return "Draw at least one Cutout first.";
        if (CutoutStates(pieces) is not { Count: > 0 } states)
            return "The masks could not be drawn.";

        var preset = new StylePreset
        {
            Layout = new LayoutSection
            {
                CenterZoom = Math.Clamp(zoom, SmallestTemplateWidth, LargestTemplateWidth),
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
            AtomicFile.WriteAllText(Path.Combine(AppPaths.Styles, file), JsonSerializer.Serialize(preset, LayoutJsonOptions), durable: true);

            // Put it first among the drop overlay presets and ensure the overlay shows at least one.
            AppSettings.Update(settings =>
            {
                settings.SplashStylePresets = [file, .. settings.SplashStylePresets.Where(s => !string.Equals(s, file, StringComparison.OrdinalIgnoreCase))];
                settings.SplashPresetCount = Math.Max(settings.SplashPresetCount, 1);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Save the Game Preset {file}", ex);
            return $"The Game Preset could not be saved: {ex.Message}";
        }

        RefreshStylePresets();
        StatusText = $"Game Preset \"{safe}\" saved: drop a video on it, or pick it from the Game Presets.";
        return null;
    }
}
