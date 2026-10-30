using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services;

// ----- The graph builder's picture: video filters, the frame engine (zones 1-4), effect stacks, layers and
// their sources and masks, motion as expressions, picture fades and transitions, and caption overlays -----
public static partial class FilterGraphBuilder
{
    private sealed partial class Pass
    {
        // ----- Video filters -----

        /// <summary>
        /// The per-frame chain applied to re-encoded video. Order matters: deinterlace whole frames, crop,
        /// clean up what is left, resize, flag the pixel shape, then convert the colours.
        /// </summary>
        /// <param name="labelSuffix">
        /// Makes the labels inside a step unique. Render Preview of several stretches writes the chain once per
        /// stretch into one graph, where two pieces may not share a label.
        /// </param>
        /// <param name="from">
        /// With the Layer Engine: where on the sequence the stretch of picture being composed begins, and
        /// (<paramref name="to"/>) where it ends. Every layer is placed in time by them.
        /// </param>
        private List<string> BuildVideoFilters(string labelSuffix, double from = 0, double to = 0)
        {
            var filters = new List<string>();
            if (Deinterlace)
                filters.Add("yadif");

            // The crop is written as fractions of the input frame, so the same settings cut the same part out
            // of a source of any resolution. Width and height are kept even, as most encoders need.
            string? crop = null;
            if (HasCrop)
                crop = CropFilter(GetCropFractions(), (SourceWidth, SourceHeight));

            // A main video in several clips has no one time of its own left once it is composed. Its burned-in
            // subtitles are drawn on its own frames first, in the file's time (which a preview's input has been
            // wound forward in).
            if (FrameEngine && IsMainSpliced && BuildSubtitleBurnFilters(LocalMediaPath) is { Count: > 0 } early)
            {
                if (_inputOrigin > 0.001)
                    filters.Add($"setpts=PTS{Signed(_inputOrigin)}/TB");
                filters.AddRange(early);
                if (_inputOrigin > 0.001)
                    filters.Add($"setpts=PTS{Signed(-_inputOrigin)}/TB");
            }

            if (FrameEngine)
            {
                filters.Add(BuildFrameEngine(crop, labelSuffix, from, to));
                if (Denoise)
                    filters.Add("hqdn3d");
                filters.Add("setsar=1");
            }
            else
            {
                if (crop is not null)
                    filters.Add(crop);
                if (Denoise)
                    filters.Add("hqdn3d");

                var hasWidth = int.TryParse(OutputWidth, out var width) && width > 0;
                var hasHeight = int.TryParse(OutputHeight, out var height) && height > 0;

                // A vertical size is not the source's shape, so both sides are spelled out; otherwise a side
                // left blank follows the other in the source's shape (-2).
                if (UseVerticalResolution && GetRequestedSize() is { } vertical)
                    filters.Add($"scale={vertical.Width}:{vertical.Height}");
                else if (hasWidth || hasHeight)
                    filters.Add($"scale={(hasWidth ? width : -2)}:{(hasHeight ? height : -2)}");

                if (GetSampleAspectRatio() is { } sampleAspectRatio)
                    filters.Add($"setsar={sampleAspectRatio}");
            }

            if (S.TonemapToSdr)
                filters.Add(TonemapFilters);

            if (!string.IsNullOrWhiteSpace(LutPath))
                filters.Add($"lut3d=file='{EscapeFilterPath(LutPath)}'");

            // Colour correction, then sharpening last of all, so that it sharpens the picture as it will be seen.
            filters.AddRange(BuildColorFilters());

            // The Sequence Output's own effects, in stack order, on the finished picture. Its keyframes and ranges are
            // in sequence seconds; this stretch's clock starts at its beginning.
            if (T.SequenceEffects.Count > 0)
            {
                var (outWidth, outHeight) = (Math.Min(FrameWidth, SourceWidth > 0 ? SourceWidth : FrameWidth), Math.Min(FrameHeight, SourceHeight > 0 ? SourceHeight : FrameHeight));
                var context = EffectContext(outWidth, outHeight, -from);
                var output = new List<(string Filter, bool Geometric)>();
                foreach (var effect in T.SequenceEffects)
                    AppendEffect(output, effect, EffectZone.Look, context);
                filters.AddRange(output.Select(f => f.Filter));
            }

            return filters;
        }

        /// <summary>
        /// A crop of a picture, given as fractions of its frame (left, top, right, bottom): even sides, as most encoders need.
        /// The main video's, and every clip's own (4.4-BugHunt: every clip's crop reframes its picture). Its sides are never
        /// cut to nothing (<see cref="EvenCut"/>); <paramref name="known"/> is the picture's own size, where it is known.
        /// </summary>
        private string CropFilter((double Left, double Top, double Right, double Bottom) crop, (int Width, int Height) known) =>
            $"crop={EvenCut("iw", 1 - crop.Left - crop.Right, known.Width)}:{EvenCut("ih", 1 - crop.Top - crop.Bottom, known.Height)}:trunc(iw*{Fraction(crop.Left)}):trunc(ih*{Fraction(crop.Top)})";

        /// <summary>
        /// One side of a cut taken out of a picture's frames by a fraction of them, even (trunc(iw*F/2)*2), as a crop and
        /// the pieces cut from the main video (a game style's HUD) are written. Held to 2 pixels at least wherever it could
        /// come out at nothing, which FFmpeg refuses ("Invalid too big or non positive size"), taking the whole graph down
        /// with it: where the picture's own size (<paramref name="known"/>) leaves less than 2, or is not known, so a valid
        /// export is written as it always was. (4.4-BugHunt held every cut of the libmpv player's live graphs so: a piece a
        /// fifth of the source high was cut 0 high from its 16 by 8 stand-in clock.) The fraction is at most 1: never more
        /// than the picture.
        /// </summary>
        /// <param name="side">iw or ih.</param>
        private string EvenCut(string side, double fraction, int known)
        {
            var written = Fraction(fraction);
            var cut = $"trunc({side}*{written}/2)*2";
            var guard = known <= 0 || Math.Truncate(known * double.Parse(written, CultureInfo.InvariantCulture) / 2) * 2 < 2;
            return guard ? $"'max(2,{cut})'" : cut;
        }

        /// <summary>
        /// The crop a layer made of the main video's picture is cut by (4.4): its own (<see cref="Lowered.StreamCrops"/>; none
        /// when that crop is nothing), or else <paramref name="sequenceCrop"/>, the sequence's.
        /// </summary>
        private string? StreamCropOf(Layer layer, string? sequenceCrop) =>
            T.StreamCrops.TryGetValue(layer, out var own)
                ? own.Left + own.Top + own.Right + own.Bottom > 1e-9 ? CropFilter(own, (SourceWidth, SourceHeight)) : null
                : sequenceCrop;

        /// <summary>
        /// The crop a layer's picture is cut by, as fractions of it: a clip of the main video's own (<see cref="Lowered.StreamCrops"/>)
        /// or, riding the chain's stream, the sequence's; any other clip its own. A mask of the layer's picture is cut the same
        /// way, so it stays where it was on the picture (4.4.0, the user's B4: the main video's mask too).
        /// </summary>
        private (double Left, double Top, double Right, double Bottom) PictureCropOf(Layer layer) =>
            T.StreamCrops.TryGetValue(layer, out var own) ? own
            : layer.IsMainVideo || T.Riders.Contains(layer) ? GetCropFractions()
            : layer.CropFractions;

        /// <summary>
        /// The crop a picture read from a file (a video, an image) is cut by before it is scaled to its box (4.4-BugHunt, the
        /// user's Q2: every clip's crop reframes its picture, on every track): a clip of the main video laid as a file, its
        /// own (<see cref="StreamCropOf"/>); any other clip, its own crop, as fractions of its file's picture. None uncropped.
        /// </summary>
        private string? FileCropOf(Layer layer) =>
            T.StreamCrops.ContainsKey(layer) ? StreamCropOf(layer, null)
            : layer.IsCropped ? CropFilter(layer.CropFractions, (layer.ImageWidth, layer.ImageHeight)) : null;

        /// <summary>
        /// A frame-filling copy of a video (<see cref="Lowered.BlurTwins"/>) scaled to cover its box (the frame) about its
        /// middle, cut to it and blurred as the blurred background is (<see cref="BackgroundRecipe"/>, without its cards).
        /// </summary>
        private string TwinBlur(int width, int height) => "," + BackgroundRecipe(width, height, 0, cards: false);

        /// <summary>
        /// The blurred background's recipe (5.0-Release: the main video's video-only child), as one chain of filters: the
        /// picture scaled to cover the frame enlarged by its zoom, cut to the frame about its middle, box-blurred (held to what
        /// the colour planes allow; none below 0.05, sharp, as on the GPU) so many passes on the graphics card where it can
        /// be, darkened, then its own Look cards (<paramref name="cards"/>; their keys and ranges in seconds of the sequence,
        /// which begins at <paramref name="sequenceStart"/> on this picture's clock). DrawBlurFill draws the same. Its radius
        /// is boxblur's whole one (half a pixel up, so no radius but 0 is ever sharp), except that a radius with a fraction
        /// below <see cref="FractionalBoxesBelow"/> pixels is PSBoxBlur's very boxes (<see cref="FractionalBoxes"/>).
        /// </summary>
        private string BackgroundRecipe(int width, int height, double sequenceStart, bool cards)
        {
            var zoom = Math.Clamp(double.IsFinite(S.BackgroundZoom) ? S.BackgroundZoom : 1, 1, 4);
            var (coverWidth, coverHeight) = zoom > 1.0005 ? (Even(width * zoom), Even(height * zoom)) : (width, height);
            var recipe = new StringBuilder($"scale={coverWidth}:{coverHeight}:force_original_aspect_ratio=increase,crop={width}:{height}");

            var radius = Math.Min(Math.Clamp(double.IsFinite(BlurRadius) ? BlurRadius : 20, 0, EffectRegistry.BackgroundMostRadius),
                Math.Max(Math.Min(width, height) / 4 - 1, 1));
            var passes = Math.Clamp(BlurPasses, 1, 4);
            var whole = Math.Round(radius, MidpointRounding.AwayFromZero);
            if (radius >= 0.05 && Math.Abs(radius - whole) >= 0.001 && radius < FractionalBoxesBelow)
            {
                foreach (var pass in FractionalBoxes(radius, passes))
                    recipe.Append(',').Append(pass);
            }
            else if (radius >= 0.05)
            {
                var box = (int)Math.Max(whole, 1);
                recipe.Append(S.OpenClBlur
                    ? $",format=yuv420p,hwupload,boxblur_opencl={box}:{passes},hwdownload,format=yuv420p"
                    : $",boxblur={box}:{passes}");
            }

            if (Math.Clamp(BackgroundDim, -0.5, 0) is < 0 and var dim)
                recipe.Append($",eq=brightness={Number(dim)}");

            if (cards && S.BackgroundEffects.Count > 0)
            {
                var context = EffectContext(width, height, sequenceStart);
                var filters = new List<(string Filter, bool Geometric)>();
                foreach (var state in S.BackgroundEffects)
                    AppendEffect(filters, EffectInstance.FromState(state), EffectZone.Look, context);
                foreach (var (filter, _) in filters)
                    recipe.Append(',').Append(filter);
            }

            return recipe.ToString();
        }

        /// <summary>
        /// Below this radius (output pixels) the GPU blurs the blurred background at the frame's own size (SceneRenderer
        /// BlurFillShrink), so its boxes are the frame's; and so many passes of them fit convolution's 49 weights.
        /// </summary>
        private const double FractionalBoxesBelow = 6;

        /// <summary>
        /// The blurred background's boxes for a radius with a fraction, as PSBoxBlur draws them: weight 1 out to the whole
        /// radius and the fraction on the next pixel each side, run <paramref name="passes"/> times across and down; as one row
        /// and one column of convolution on each colour plane (boxblur's planes), its weights in thousandths (convolution takes
        /// whole numbers) over their sum.
        /// </summary>
        private static string[] FractionalBoxes(double radius, int passes)
        {
            var whole = (int)Math.Floor(radius);
            var box = new double[2 * whole + 3];
            Array.Fill(box, 1.0);
            box[0] = box[^1] = radius - whole;

            double[] weights = [1];
            for (var p = 0; p < passes; p++)
            {
                var next = new double[weights.Length + box.Length - 1];
                for (var i = 0; i < weights.Length; i++)
                {
                    for (var k = 0; k < box.Length; k++)
                        next[i + k] += weights[i] * box[k];
                }

                weights = next;
            }

            // In thousandths, the ends that come to nothing left out (the weights are the same from either end).
            var sum = weights.Sum();
            var thousandths = weights.Select(w => (int)Math.Round(1000 * w / sum)).ToArray();
            var cut = 0;
            while (cut < thousandths.Length / 2 && thousandths[cut] == 0)
                cut++;
            var kept = thousandths[cut..^cut];
            var matrix = string.Join(" ", kept.Select(w => w.ToString(CultureInfo.InvariantCulture)));
            var divide = (1.0 / kept.Sum()).ToString("0.############", CultureInfo.InvariantCulture);
            string Pass(string mode) =>
                $"convolution=0m='{matrix}':1m='{matrix}':2m='{matrix}':0rdiv={divide}:1rdiv={divide}:2rdiv={divide}:0mode={mode}:1mode={mode}:2mode={mode}";
            return [Pass("row"), Pass("column")];
        }

        // A preview encode opens the main video's file already wound forward to where it begins; the file's frames
        // then count from there, and this is how far in that is.
        private double _inputOrigin;

        /// <summary>
        /// The rate the sequence's canvas ticks at: the timeline's, exactly, so that frames of that rate fall one to a tick.
        /// Written as a whole number, or a decimal where it is one (12.5, 47.952), or else as its fraction (an NTSC rate is
        /// 30000/1001, not the round number it is quoted as).
        /// </summary>
        private string CanvasRate
        {
            get
            {
                // (A rate that is not positive counts as 60/1, as everywhere in the timeline's model.)
                var rate = S.Timeline.FrameRate.IsValid ? S.Timeline.FrameRate : Models.Timeline.FrameRate.Fps60;
                if (rate.Denominator == 1)
                    return rate.Numerator.ToString(CultureInfo.InvariantCulture);
                return 1000 % rate.Denominator == 0
                    ? Number(rate.Numerator / (double)rate.Denominator)
                    : string.Create(CultureInfo.InvariantCulture, $"{rate.Numerator}/{rate.Denominator}");
            }
        }

        /// <summary>A number with its sign always written, to follow PTS in a setpts expression.</summary>
        private static string Signed(double value) => (value < 0 ? "-" : "+") + Number(Math.Abs(value));

        /// <summary>The enable option for a step that is only applied while every one of the given conditions holds; nothing when there are none.</summary>
        private static string Enabled(string? first, string? second = null) =>
            first is null && second is null ? "" : $":enable='{(first is not null && second is not null ? $"{first}*{second}" : first ?? second)}'";

        /// <summary>
        /// Composes one stretch of the sequence. The base is the sequence's own canvas: black, the size of the
        /// project, as long as the stretch, with a clock that belongs to nothing that is laid on it. Every track
        /// is laid on it in stacking order, the main video being one of them: cut to the part of its file this
        /// stretch shows, and set down at the moment it comes in. Before it starts and after it ends the canvas
        /// simply goes on without it. A layer may lie partly or wholly outside the canvas: it is cut off at the edge.
        ///
        /// The main video's frames are split into independent branches, one per thing that shows them (the
        /// blurred background, the main video itself, the pieces cut from it); nothing done to an upper layer can
        /// reach the ones below, because by then they are a finished picture. One shortcut is kept: the blurred
        /// background is not made at all while the main video would hide every pixel of it.
        ///
        /// A main video whose length is not known is the exception to the canvas: its own frames are the clock there, so
        /// the canvas is made from them and what comes from the main video is switched off outside the main video's
        /// stretch instead of being cut to it.
        /// </summary>
        /// <param name="from">Where on the sequence the stretch begins, in seconds: the clock of what is composed starts there.</param>
        /// <param name="to">Where it ends.</param>
        private string BuildFrameEngine(string? centerCrop, string s, double from, double to)
        {
            var (width, height) = (FrameWidth, FrameHeight);
            var full = GetCenterRect();
            var center = (X: full.X, Y: full.Y, Width: full.Width, Height: full.Height);
            var main = T.MainLayer;

            // How much of this stretch the main video is there for. A file whose length is not known is taken to
            // be all of it, and lends the canvas its clock.
            var (seenFrom, seenTo) = (from, to);
            var seen = GetSeenMain(seenFrom, seenTo);
            var known = seenTo - seenFrom > 0.001 && MainMediaSeconds > 0;

            // (With no clip of the primary chain on the timeline at all, nothing comes from its stream, known or not.)
            var mainSeen = GetMainClips().Count > 0 && (!known || seen.Count > 0);

            // The chain's picture in one piece is there while its own clips are; the chain's other clips ride alone (4.3D).
            var whole = ReferenceEquals(T.CenterClips, T.MainClips);
            var centerSeen = mainSeen && (whole || (T.CenterClips.Count > 0 && (!known || SeenOf(T.CenterClips, seenFrom, seenTo).Count > 0)));
            var throughout = !known;
            if (known && seen.Count > 0 && seen[0].From - seenFrom < 0.001 && seenTo - seen[^1].To < 0.001)
            {
                throughout = true;
                for (var i = 1; i < seen.Count && throughout; i++)
                    throughout = seen[i].From - seen[i - 1].To < 0.001;
            }

            var clockFromInput = !known;

            // The stack, bottom first, without the layers that have nothing to show.
            var stack = GetStack().Where(l => l.IsMainVideo ? !l.IsHidden && centerSeen
                : l.IsUsable && (!l.IsVideo || mainSeen)).ToList();
            var motions = stack.ConvertAll(l => GetMotion(l, width, height, from));

            // Each one's fades, and what its slides and zooms do to where it is laid and how large (an adjustment layer's
            // fades are its effects coming and going: see AppendAdjustment).
            var fades = stack.ConvertAll(l => l.IsAdjustment ? [] : PictureFades(l, from));
            for (var n = 0; n < stack.Count; n++)
            {
                if (fades[n].Count > 0)
                    motions[n] = WithTransitions(motions[n], fades[n], stack[n].IsMainVideo ? center : stack[n].GetOutputRect(width, height, SourceWidth, SourceHeight), width, height);
            }

            var covers = center.X <= 0 && center.Y <= 0 && center.X + center.Width >= width && center.Y + center.Height >= height;
            var hidesBackground = whole && stack.Count > 0 && stack[0].IsMainVideo && covers && throughout && IsOpaque(main) && !motions[0].Any;

            // The blurred background is made under the chain's clips that want it (4.4: each clip's blur fill, the sequence's
            // switch for those that leave it to it): all of them, as it always was, while every clip does.
            var blurClips = S.BackgroundHidden ? T.ForcedBlurClips : T.BlurClips;
            var blurAll = ReferenceEquals(blurClips, T.MainClips);
            var blurred = mainSeen && !hidesBackground && (blurAll || (blurClips.Count > 0 && (!known || SeenOf(blurClips, seenFrom, seenTo).Count > 0)));

            // One copy of the main video's frames for each thing that shows them. Layers from files bring their own picture.
            var branches = new List<string>();
            if (clockFromInput)
                branches.Add($"[canvas_in{s}]");
            if (blurred)
                branches.Add($"[bg_in{s}]");
            if (stack.Any(l => l.IsMainVideo))
                branches.Add($"[center_in{s}]");
            for (var n = 0; n < stack.Count; n++)
            {
                if (stack[n].IsVideo)
                    branches.Add($"[ui{n}_in{s}]");
            }

            var graph = RentBuilder();

            // The main video, cut to what this stretch shows of it and set down where it comes in. Left as it is
            // when it is the whole sequence and all of it is wanted. A crossfade between two of its clips lays the
            // later one's head over the earlier one (AppendMainCrossfades); on its own clock it is a cut.
            if (!clockFromInput && seen.Count > 0 && !(IsMainWholeSequence && from < 0.001 && to >= SequenceSeconds - 0.001 && _inputOrigin == 0))
            {
                if (GetSeenCrossfades(from, to) is { Count: > 0 } heads)
                    AppendMainCrossfades(graph, seen, heads, from, s);
                else
                    AppendMainClips(graph, seen, from, s);
            }

            // Nothing of it wanted at all: one frame is read and dropped, and the file is left alone.
            graph.Append(branches.Count switch
            {
                0 => "trim=end_frame=1,nullsink;",
                1 => $"null{branches[0]};",
                _ => $"split={branches.Count}{string.Concat(branches)};",
            });

            // The canvas. On the main video's own clock four pixels of its frame are blackened and stretched, which
            // keeps its clock: frame for frame.
            if (clockFromInput)
                graph.Append($"[canvas_in{s}]crop=2:2:0:0,format=yuv420p,lutyuv=y=16:u=128:v=128,scale={width}:{height}:flags=neighbor");
            else
                graph.Append($"color=c=black:s={width}x{height}:r={CanvasRate}:d={Number(to - from)},format=yuv420p");

            // What comes from the main video is there only while the main video is. Composed on the canvas, its
            // frames run out and the canvas carries on; on its own clock it is switched off outside its stretch.
            var mainEnding = clockFromInput ? "" : ":eof_action=pass";
            // Between two clips that do not meet there is nothing of it either, whichever clock it is on. The one piece is
            // there only for its own clips, when some of the chain's ride alone.
            var mainGate = !throughout && (clockFromInput || seen.Count > 1) ? BuildMainGate(from, to) : null;
            var centerGate = whole ? mainGate : FilterGraphBuilder.BuildMainGate(T.CenterClips, from, to);

            if (blurred)
            {
                graph.Append($"[base{s}];[bg_in{s}]");

                // Its zoom, its blur (the radius no more than half the smaller side of the colour planes, which are half size;
                // on the graphics card where it can be, OpenCL, which every vendor's driver has, else the processor), its
                // darkening and its own effects: BackgroundRecipe.
                graph.Append(BackgroundRecipe(width, height, -from, cards: true));

                // Made of the main video, it comes on and goes with it: from black, by a dissolve whatever the main video's style.
                if (stack.FindIndex(l => l.IsMainVideo) is >= 0 and var mainAt && fades[mainAt].Count > 0)
                    AppendPictureFades(graph, "bgf", s, fades[mainAt].ConvertAll(f => f with { Style = TransitionStyle.Dissolve }), width, height);

                graph.Append($"[bg{s}];[base{s}][bg{s}]overlay=0:0{mainEnding}{Enabled(blurAll ? mainGate : FilterGraphBuilder.BuildMainGate(blurClips, from, to))}");
            }

            // The layers, each on the picture built so far. The running picture is labelled between steps;
            // after the last one it is left open, so the rest of the chain continues from it.
            for (var n = 0; n < stack.Count; n++)
            {
                var (layer, motion) = (stack[n], motions[n]);

                // An adjustment layer has no picture: its effects work on the picture built so far, while it lasts.
                if (layer.IsAdjustment)
                {
                    graph.Append($"[stage{n}{s}];");
                    AppendAdjustment(graph, layer, n, s, width, height, from, PictureFades(layer, from));
                    continue;
                }

                var name = layer.IsMainVideo ? "center" : $"ui{n}";
                var (x, y, layerWidth, layerHeight) = layer.IsMainVideo
                    ? center
                    : layer.GetOutputRect(width, height, SourceWidth, SourceHeight);

                // A layer whose size is animated is built once, at the largest it gets, and scaled from there.
                var (builtWidth, builtHeight) = (layerWidth, layerHeight);
                if (motion.Width is not null)
                {
                    builtWidth = Even(motion.LargestWidth * width);
                    builtHeight = Even((double)layerHeight * builtWidth / layerWidth);
                }

                // The main video. The crop frames this layer only, and a clip of it that rides alone (by its own crop when it
                // has one, 4.4); the background uses the whole picture, and each cut-out its own part of it (its source
                // rectangle, less its own crop: AppendLayer).
                var mainSource = layer.IsMainVideo || T.Riders.Contains(layer)
                    ? $"[{name}_in{s}]{(StreamCropOf(layer, centerCrop) is { } cut ? cut + "," : "")}scale={builtWidth}:{builtHeight}"
                    : null;
                graph.Append($"[stage{n}{s}];");
                AppendLayer(graph, layer, name, s, builtWidth, builtHeight, from, motion, mainSource, fades[n]);

                // A turned picture is larger than the layer it was; it stays where its middle was, or, turned about an
                // anchor point that is not its middle, where that point puts its middle (see Pivot).
                var (boxWidth, boxHeight) = GetBox(layer, motion, builtWidth, builtHeight);
                var (anchorX, anchorY) = (layer.AnchorX, layer.AnchorY);
                var pivots = layer.HasOffsetAnchor && layer.HasFilters && (motion.Angle is not null || Math.Abs(layer.Rotation % 360) >= 0.05);
                string placeX, placeY;
                if (motion.Any)
                {
                    // The box is that much larger than the layer on each side, and grows and shrinks with it.
                    var size = motion.Width is null ? "" : $"*({motion.Width})/{builtWidth}";
                    var (marginX, marginY) = ((boxWidth - builtWidth) / 2.0, (boxHeight - builtHeight) / 2.0);
                    var (widthNow, heightNow) = motion.Width is null
                        ? (Number(layerWidth), Number(layerHeight))
                        : ($"({motion.Width})", $"({motion.Width})*{Ratio((double)builtHeight / builtWidth)}");

                    // The corner: its keyframes; or, with the size animated and the place not, where it is with the anchor
                    // point held still as the layer grows and shrinks about it.
                    var cornerX = motion.X ?? (motion.Width is null ? Number(x) : $"{Number(x + anchorX * layerWidth)}-{Ratio(anchorX)}*{widthNow}");
                    var cornerY = motion.Y ?? (motion.Width is null ? Number(y) : $"{Number(y + anchorY * layerHeight)}-{Ratio(anchorY)}*{heightNow}");
                    placeX = cornerX + (marginX == 0 ? "" : $"-{Number(marginX)}{size}");
                    placeY = cornerY + (marginY == 0 ? "" : $"-{Number(marginY)}{size}");
                    if (pivots)
                    {
                        var (cosine, sine) = motion.Angle is { } angle
                            ? ($"(cos(({angle})*PI/180)-1)", $"sin(({angle})*PI/180)")
                            : ($"({Ratio(Math.Cos(layer.Rotation * Math.PI / 180) - 1)})", $"({Ratio(Math.Sin(layer.Rotation * Math.PI / 180))})");
                        var (dx, dy) = ($"({Ratio(0.5 - anchorX)}*{widthNow})", $"({Ratio(0.5 - anchorY)}*{heightNow})");
                        placeX += $"+{dx}*{cosine}-{dy}*{sine}";
                        placeY += $"+{dx}*{sine}+{dy}*{cosine}";
                    }

                    // Sliding on or off over its fades.
                    if (motion.ShiftX is { } slideX)
                        placeX += $"+({slideX})";
                    if (motion.ShiftY is { } slideY)
                        placeY += $"+({slideY})";
                }
                else
                {
                    var (px, py) = (x - (boxWidth - layerWidth) / 2.0, y - (boxHeight - layerHeight) / 2.0);
                    if (pivots)
                    {
                        var (dx, dy) = Layer.Pivot(layer.Rotation, (0.5 - anchorX) * layerWidth, (0.5 - anchorY) * layerHeight);
                        (px, py) = (px + dx, py + dy);
                    }

                    (placeX, placeY) = (Number(Math.Round(px)), Number(Math.Round(py)));
                }

                string At(int offsetX, int offsetY) => motion.Any
                    ? $"x='{placeX}{Shift(offsetX)}':y='{placeY}{Shift(offsetY)}':eval=frame"
                    : $"{(int.Parse(placeX, CultureInfo.InvariantCulture) + offsetX).ToString(CultureInfo.InvariantCulture)}:{(int.Parse(placeY, CultureInfo.InvariantCulture) + offsetY).ToString(CultureInfo.InvariantCulture)}";

                var ending = new StringBuilder();
                if (layer.IsMainVideo || layer.IsVideo)
                    ending.Append(mainEnding);

                // A video from a file ends as its source does (see AppendVideoFile): a clip with a length goes when
                // it is over and the picture under it carries on, one that repeats lasts as long as the picture
                // under it. So does a still that was made into a stream to be animated.
                if (layer.IsVideoFile)
                    ending.Append(_videoEnding);
                else if (layer.IsStill && motion.Reshapes)
                    ending.Append(":shortest=1");

                // A layer with a time to appear and a time to go is only laid on between the two. The clock the
                // filter sees starts at this stretch of the picture, so the times are counted from there.
                string? timed = null;
                if (!layer.IsMainVideo && layer.HasTiming && (layer.StartTime > 0.001 || layer.Duration > 0.001))
                {
                    var appears = layer.StartTime - from;
                    timed = $"between(t,{Number(appears)},{(layer.Duration > 0.001 ? Number(appears + layer.Duration) : "1e9")})";
                }

                ending.Append(Enabled(layer.IsMainVideo ? centerGate : layer.IsVideo ? mainGate : null, timed));

                var (under, top) = ($"stage{n}{s}", $"{name}_layer{s}");
                if (layer.Shadow && layer.FxOn(EffectRegistry.Shadow))
                {
                    // The shadow is the layer's own outline in its colour at reduced opacity, feathered, laid down
                    // first and moved along the light's angle (4.1.1-B).
                    var (shadowChain, shadowX, shadowY) = ShadowOf(layer);
                    graph.Append($";[{name}_layer{s}]split[{name}_top{s}][{name}_shadow_in{s}]");
                    graph.Append($";[{name}_shadow_in{s}]{shadowChain}[{name}_shadow{s}]");
                    graph.Append($";[stage{n}{s}][{name}_shadow{s}]overlay={At(shadowX, shadowY)}{ending}[{name}_shaded{s}]");
                    (under, top) = ($"{name}_shaded{s}", $"{name}_top{s}");
                }

                // Zone 4's blend mode: Normal is the overlay every layer always had.
                if (layer.BlendModeName is "multiply" or "screen" or "addition" or "overlay" or "softlight" or "hardlight" or "difference" && !layer.IsMainVideo)
                    AppendBlend(graph, under, top, $"{name}{s}", $"{At(0, 0)}{ending}", layer.BlendModeName);
                else
                    graph.Append($";[{under}][{top}]overlay={At(0, 0)}{ending}");
            }

            return ReturnBuilder(graph);
        }

        /// <summary>
        /// A layer laid on with a blend mode (zone 4): placed on a see-through copy of the picture under it, its colours
        /// mixed with that picture's in RGB, and the mix laid on through the layer's own transparency (so outside the layer,
        /// and outside its time, nothing changes). Left open at the end, as an overlay is. Amber: two passes over the frame.
        /// </summary>
        private static void AppendBlend(StringBuilder graph, string under, string top, string p, string placement, string mode)
        {
            graph.Append($";[{under}]split=3[{p}_u0][{p}_u1][{p}_u2]");
            graph.Append($";[{p}_u0]format=yuva420p,lutyuv=y=16:u=128:v=128:a=0[{p}_cv]");
            graph.Append($";[{p}_cv][{top}]overlay={placement},format=gbrap,split[{p}_lc][{p}_la]");
            graph.Append($";[{p}_la]alphaextract[{p}_lm];[{p}_lc]format=gbrp[{p}_lrgb];[{p}_u1]format=gbrp[{p}_urgb]");
            graph.Append($";[{p}_urgb][{p}_lrgb]blend=all_mode={mode}:shortest=1[{p}_mix];[{p}_mix][{p}_lm]alphamerge[{p}_mixa]");
            graph.Append($";[{p}_u2][{p}_mixa]overlay=0:0");
        }

        /// <summary>
        /// An adjustment layer: its Look effects worked on the picture built so far, while the clip lasts. Over the whole
        /// frame each filter is switched on for that stretch; within a smaller rectangle that part is cut out, worked
        /// on, and laid back for the stretch. Left open at the end, as an overlay is.
        /// </summary>
        /// <param name="fades">Its fades: its effects come on and go over them, the worked picture laid over the picture as it was by a dissolve or a wipe (a slide or a zoom is a dissolve here).</param>
        private void AppendAdjustment(StringBuilder graph, Layer layer, int n, string s, int width, int height, double from, List<PictureFade> fades)
        {
            var (x, y, w, h) = layer.GetOutputRect(width, height, SourceWidth, SourceHeight);
            var (left, top) = (Math.Clamp(x, 0, width), Math.Clamp(y, 0, height));
            var (right, bottom) = (Math.Clamp(x + w, 0, width), Math.Clamp(y + h, 0, height));
            var (cutWidth, cutHeight) = ((right - left) / 2 * 2, (bottom - top) / 2 * 2);
            var appears = layer.StartTime - from;
            var timed = $"between(t,{Number(appears)},{(layer.Duration > 0.001 ? Number(appears + layer.Duration) : "1e9")})";
            fades = fades.ConvertAll(f => TransitionStyles.IsSlide(f.Style) || f.Style == TransitionStyle.Zoom ? f with { Style = TransitionStyle.Dissolve } : f);
            var whole = left <= 0 && top <= 0 && right >= width && bottom >= height;

            if (whole && fades.Count == 0)
            {
                var filters = BuildLookFilters(layer, width, height, appears, timed);
                graph.Append($"[stage{n}{s}]").Append(filters.Count > 0 ? string.Join(",", filters.Select(f => f.Filter)) : "null");
                return;
            }

            if (whole)
                (left, top, cutWidth, cutHeight) = (0, 0, width, height);
            var inside = cutWidth >= 2 && cutHeight >= 2 ? BuildLookFilters(layer, cutWidth, cutHeight, appears) : new List<(string Filter, bool Geometric)>();
            if (inside.Count == 0)
            {
                graph.Append($"[stage{n}{s}]null");
                return;
            }

            graph.Append($"[stage{n}{s}]split[adj{n}_base{s}][adj{n}_cut{s}];[adj{n}_cut{s}]{(whole ? "" : $"crop={cutWidth}:{cutHeight}:{left}:{top},")}"
                         + string.Join(",", inside.Select(f => f.Filter)));
            AppendPictureFades(graph, $"adj{n}", s, fades, cutWidth, cutHeight);
            graph.Append($"[adj{n}_fx{s}];[adj{n}_base{s}][adj{n}_fx{s}]overlay={left}:{top}:enable='{timed}'");
        }

        // One builder is kept for each thread and used again: a graph is some kilobytes, written many times a
        // second while a layer is dragged, and the room for it is not asked for afresh each time.
        [ThreadStatic] private static StringBuilder? _pooledBuilder;

        private static StringBuilder RentBuilder()
        {
            var builder = _pooledBuilder ?? new StringBuilder(4096);
            _pooledBuilder = null;
            return builder.Clear();
        }

        private static string ReturnBuilder(StringBuilder builder)
        {
            var text = builder.ToString();
            if (builder.Capacity <= 256 * 1024)
                _pooledBuilder = builder;
            return text;
        }

        /// <summary>The parts of the main video's clips that fall inside a stretch of the sequence, in order, each with the moment of the file it begins at.</summary>
        private List<(double From, double To, double Source)> GetSeenMain(double from, double to) => SeenOf(GetMainClips(), from, to);

        /// <summary>The same for where the primary file's sound is heard (<see cref="GetMainSound"/>): its clips' J and L cuts and slips moved in.</summary>
        private List<(double From, double To, double Source)> GetSeenSound(double from, double to) => SeenOf(GetMainSound(), from, to);

        private static List<(double From, double To, double Source)> SeenOf(IReadOnlyList<MainClip> clips, double from, double to)
        {
            var seen = new List<(double From, double To, double Source)>();
            foreach (var clip in clips)
            {
                var (a, b) = (Math.Max(from, clip.Start), Math.Min(to, clip.End));
                if (b - a > 0.001)
                    seen.Add((a, b, clip.Offset + a - clip.Start));
            }

            return seen;
        }

        /// <summary>The crossfades between the main video's clips that fall inside a stretch of the sequence: the part of each that does, and the whole of it.</summary>
        private List<((double From, double To, double Source) Seen, MainCrossfade Whole)> GetSeenCrossfades(double from, double to)
        {
            var seen = new List<((double From, double To, double Source) Seen, MainCrossfade Whole)>();
            foreach (var crossfade in T.MainCrossfades)
            {
                var (a, b) = (Math.Max(from, crossfade.From), Math.Min(to, crossfade.To));
                if (b - a > 0.001)
                    seen.Add(((a, b, crossfade.Source + a - crossfade.From), crossfade));
            }

            return seen;
        }

        /// <summary>
        /// The main video's clips (AppendMainClips) with each crossfade between two of them laid over: the file is read
        /// once more for each crossfade, cut to the later clip's head, and that comes up over the earlier clip by an
        /// opacity ramp from the crossfade's start to its end, where the later clip itself takes over. Leaves the chain
        /// open, as AppendMainClips does.
        /// </summary>
        private void AppendMainCrossfades(StringBuilder graph, List<(double From, double To, double Source)> seen,
            List<((double From, double To, double Source) Seen, MainCrossfade Whole)> heads, double from, string s)
        {
            graph.Append("split=").Append(heads.Count + 1).Append($"[mxa{s}]");
            for (var k = 0; k < heads.Count; k++)
                graph.Append($"[mxh{k}{s}]");
            graph.Append($";[mxa{s}]");
            AppendMainClips(graph, seen, from, s);
            graph.Append($"null[mxm{s}]");

            for (var k = 0; k < heads.Count; k++)
            {
                var (part, whole) = heads[k];
                graph.Append($";[mxh{k}{s}]");
                AppendMainClips(graph, [part], from, $"{s}h{k}");

                // The ramp runs over the whole crossfade, from where it begins (or this stretch does, when it began before).
                var start = Math.Max(whole.From - from, 0);
                graph.Append("format=yuva420p,fade=t=in:st=").Append(Number(start)).Append(":d=").Append(Number(Math.Max(whole.To - from - start, 0.001)))
                    .Append($":alpha=1[mxf{k}{s}]");
            }

            for (var k = 0; k < heads.Count; k++)
            {
                var (part, _) = heads[k];
                graph.Append(';').Append(k == 0 ? $"[mxm{s}]" : $"[mxo{k - 1}{s}]").Append($"[mxf{k}{s}]overlay=format=auto:eof_action=pass:enable='between(t,")
                    .Append(Number(part.From - from)).Append(',').Append(Number(part.To - from)).Append(")'");
                graph.Append(k == heads.Count - 1 ? "," : $"[mxo{k}{s}]");
            }
        }

        /// <summary>
        /// Makes the main video's frames a picture in the sequence's time: every clip cut out of the file and set
        /// down at the moment it comes in. One clip is a trim. Several clips that take the file in the order it
        /// runs are one pass over it: the frames of every clip are let through and the rest dropped (select), and
        /// each frame is then moved to where its clip puts it (setpts, by which clip its own time falls in).
        /// Nothing is held in memory for that. Clips that take the file out of order (the end of it placed before
        /// the beginning) cannot be had in one pass: each is cut on a copy of its own and the copies are woven
        /// together by time, which holds frames back for as long as the order is wrong.
        /// </summary>
        private void AppendMainClips(StringBuilder graph, List<(double From, double To, double Source)> seen, double from, string s)
        {
            var origin = _inputOrigin;
            if (seen.Count == 1)
            {
                var (cutFrom, lead) = (seen[0].Source - origin, seen[0].From - from);
                graph.Append("trim=");
                if (cutFrom > 0.001)
                    graph.Append("start=").Append(Number(cutFrom)).Append(':');
                graph.Append("end=").Append(Number(cutFrom + seen[0].To - seen[0].From)).Append(",setpts=PTS-STARTPTS");
                if (lead > 0.001)
                    graph.Append('+').Append(Number(lead)).Append("/TB");
                graph.Append(',');
                return;
            }

            var inOrder = true;
            for (var i = 1; i < seen.Count && inOrder; i++)
                inOrder = seen[i].Source >= seen[i - 1].Source + (seen[i - 1].To - seen[i - 1].From) - 0.001;

            if (inOrder)
            {
                graph.Append("select='");
                for (var i = 0; i < seen.Count; i++)
                {
                    var start = seen[i].Source - origin;
                    graph.Append(i > 0 ? "+" : "").Append("gte(t,").Append(Number(start)).Append(")*lt(t,").Append(Number(start + seen[i].To - seen[i].From)).Append(')');
                }

                // How far each clip's frames are moved: to where the clip is on this stretch, from where they are in the file.
                graph.Append("',setpts='PTS+(");
                var before = 0.0;
                for (var i = 0; i < seen.Count; i++)
                {
                    var shift = seen[i].From - from - (seen[i].Source - origin);
                    if (i == 0)
                        graph.Append(Number(shift));
                    else if (Math.Abs(shift - before) >= 0.0005)
                        graph.Append(Signed(shift - before)).Append("*gte(T,").Append(Number(seen[i].Source - origin)).Append(')');
                    before = shift;
                }

                graph.Append(")/TB',");
                return;
            }

            graph.Append("split=").Append(seen.Count);
            for (var i = 0; i < seen.Count; i++)
                graph.Append($"[mcut{i}{s}]");
            for (var i = 0; i < seen.Count; i++)
            {
                var start = seen[i].Source - origin;
                graph.Append($";[mcut{i}{s}]trim=start={Number(start)}:end={Number(start + seen[i].To - seen[i].From)},setpts=PTS-STARTPTS{Signed(seen[i].From - from)}/TB[mset{i}{s}]");
            }

            graph.Append(';');
            for (var i = 0; i < seen.Count; i++)
                graph.Append($"[mset{i}{s}]");
            graph.Append("interleave=n=").Append(seen.Count).Append(',');
        }

        /// <summary>
        /// The size of the picture a layer is laid on the frame as: the layer itself, the box its corners reach
        /// when it is turned, or, when its turning is animated, the square it can turn all the way round in.
        /// </summary>
        private static (int Width, int Height) GetBox(Layer layer, Motion motion, int width, int height)
        {
            if (motion.Angle is null)
                return GetTurnedSize(layer, width, height);

            var across = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height) / 2) * 2;
            return (across, across);
        }

        /// <summary>
        /// Whether a layer hides everything under the rectangle it covers: solid, square-cornered, unturned, with nothing keyed
        /// or masked out. (A crop cuts the picture, which still fills its box: 4.4-BugHunt.)
        /// </summary>
        private bool IsOpaque(Layer layer) =>
            layer.Opacity >= 100 && !layer.HasKeys(KeyProperty.Opacity) && !layer.ChromaKey && !(layer.CustomMask && Exists(layer.MaskPath.Trim().Trim('"')))
            && layer.CornerRadius == 0 && !layer.Feather && Math.Abs(layer.Rotation % 360) < 0.05;

        /// <summary>The size of the picture once it has been turned: the box its corners then reach, in even pixels.</summary>
        private static (int Width, int Height) GetTurnedSize(Layer layer, int width, int height)
        {
            if (!layer.HasFilters || Math.Abs(layer.Rotation % 360) < 0.05)
                return (width, height);

            var angle = layer.Rotation * Math.PI / 180;
            var (cos, sin) = (Math.Abs(Math.Cos(angle)), Math.Abs(Math.Sin(angle)));
            return ((int)Math.Ceiling((width * cos + height * sin) / 2) * 2, (int)Math.Ceiling((width * sin + height * cos) / 2) * 2);
        }

        // ----- Effect stacks (Models/Effects) -----

        /// <summary>What an effect's compile is told: the picture's size, the frame's, and the clip's clock.</summary>
        private EffectCompileContext EffectContext(int width, int height, double clipStart) => new()
        {
            Width = width, Height = height, FrameWidth = FrameWidth, ClipStart = clipStart,
            Exists = Exists, EscapePath = EscapeFilterPath, KeyExpression = (keys, start) => KeyExpression(keys, start, 1),
            InstanceName = $"fx{++_effectInstances}",
        };

        // How many effect contexts have been made for this graph: each gives its filters a name of their own (sendcmd).
        private int _effectInstances;

        /// <summary>
        /// A card's filters, if it belongs to the zone being compiled and does anything: switched off, a missing effect,
        /// values that change nothing emit nothing. A card with a
        /// range is only at work inside it; <paramref name="gate"/> limits it further (an adjustment layer's own stretch).
        /// </summary>
        private void AppendEffect(List<(string Filter, bool Geometric)> filters, EffectInstance effect, EffectZone zone, EffectCompileContext context, string? gate = null)
        {
            if (!effect.Enabled || effect.Definition is not { Compile: { } compile } definition || definition.Zone != zone)
                return;
            if (compile(context, effect) is not { Count: > 0 } compiled)
                return;

            var range = effect.Range is { } window
                ? $"between(t,{Number(context.ClipStart + window.Start)},{Number(context.ClipStart + window.End)})"
                : null;
            // sendcmd has no timeline: it is never switched off, and the filter it talks to is. Nor has format (Color Balance
            // works in 16-bit RGB, 5.0-AudioAnd3D): the picture stays converted outside the stretch, which changes nothing.
            var when = range is not null && gate is not null ? $"{range}*{gate}" : range ?? gate;
            foreach (var filter in compiled)
            {
                var untimed = filter.StartsWith("sendcmd=", StringComparison.Ordinal) || filter.StartsWith("format=", StringComparison.Ordinal);
                filters.Add((when is null || untimed ? filter : WithOption(filter, $"enable='{when}'"), definition.Geometric));
            }
        }

        /// <summary>One more option on a filter: after its others, or as its first.</summary>
        private static string WithOption(string filter, string option) => filter.Contains('=') ? $"{filter}:{option}" : $"{filter}={option}";

        /// <summary>A layer's Look effects (zone 2), in stack order: each filter, and whether it moves pixels (a flip).</summary>
        /// <param name="clipStart">Where the clip begins on the clock of the picture being composed.</param>
        private List<(string Filter, bool Geometric)> BuildLookFilters(Layer layer, int width, int height, double clipStart, string? gate = null)
        {
            var filters = new List<(string Filter, bool Geometric)>();
            if (!layer.HasFilters || layer.EffectsBypassed)
                return filters;

            var context = EffectContext(width, height, clipStart);
            foreach (var effect in layer.Effects)
                AppendEffect(filters, effect, EffectZone.Look, context, gate);
            return filters;
        }

        /// <summary>
        /// The Key and Matte zone (3), in stack order. The key writes the transparency itself; everything else works on
        /// it: consecutive matte steps share one split, alphaextract ... alphamerge, the mask and the edges multiplying
        /// it (one group, where the first of their cards is) and the choke and smoothing filtering it. A layer that only has
        /// the old key, mask and edges compiles exactly as before stacks. (The crop is no mask: it cut the picture before
        /// it got here, 4.4-BugHunt.)
        /// </summary>
        private void AppendKeyAndMatte(StringBuilder chain, Layer layer, string name, string s, int width, int height, double clipStart)
        {
            var context = EffectContext(width, height, clipStart);
            var formatted = false;
            var run = new List<string>();
            var sources = new StringBuilder();
            var runs = 0;
            var maskPlaced = false;

            void Format()
            {
                if (!formatted)
                    chain.Append(",format=yuva420p");
                formatted = true;
            }

            // The run's labels: the first run keeps the names the mask chain always had.
            string Prefix() => runs == 0 ? name : $"{name}r{runs}";

            void Flush()
            {
                if (run.Count == 0)
                    return;

                Format();
                var p = Prefix();
                chain.Append($",split[{p}_pic{s}][{p}_a_in{s}];[{p}_a_in{s}]alphaextract[{p}_a0{s}]");
                chain.Append(sources);
                for (var k = 0; k < run.Count; k++)
                    chain.Append($";[{p}_a{k}{s}]{run[k]}[{p}_a{k + 1}{s}]");
                chain.Append($";[{p}_pic{s}][{p}_a{run.Count}{s}]alphamerge");
                run.Clear();
                sources.Clear();
                runs++;
            }

            // The mask and the edges: grey pictures the size of the layer, white where it is to show, each made once
            // and held, not worked out again for every frame (see StillMask). With a mask, Soft Edges softens the mask's
            // outline, and the rectangle only has its corners rounded.
            void PlaceMasks()
            {
                maskPlaced = true;
                var p = Prefix();
                var maskFile = layer.CustomMask && layer.HasMask && layer.FxOn(EffectRegistry.Mask) ? layer.MaskPath.Trim().Trim('"') : "";
                var hasMask = maskFile.Length > 0 && Exists(maskFile);
                if (hasMask)
                {
                    sources.Append(';').Append(BuildMaskSource(layer, maskFile, S.AlphaMasks.Contains(maskFile), width, height, 1, (SourceWidth, SourceHeight), PictureCropOf(layer)))
                        .Append(StillMask).Append($"[{p}_cmask{s}]");
                    run.Add(MultiplyByStill($"{p}_cmask{s}"));
                }

                if (BuildLayerMask(layer, width, height, 1, softEdge: !hasMask) is { } shape)
                {
                    sources.Append($";color=c=black:s={width}x{height}:r=1,format=gray,geq=lum='255*{shape}'").Append(StillMask).Append($"[{p}_smask{s}]");
                    run.Add(MultiplyByStill($"{p}_smask{s}"));
                }
            }

            IEnumerable<EffectInstance> cards = layer.EffectsBypassed ? [] : layer.Effects;
            foreach (var effect in cards)
            {
                if (!effect.Enabled || effect.Definition is not { Zone: EffectZone.KeyAndMatte } definition)
                    continue;

                switch (definition.Type)
                {
                    case EffectRegistry.ChromaKey when layer.ChromaKey && layer.HasKeying:
                    {
                        Flush();
                        var key = KeyColor(layer.ChromaColor);
                        var gate = effect.Range is { } window ? $":enable='between(t,{Number(clipStart + window.Start)},{Number(clipStart + window.End)})'" : "";

                        // Similarity and Blend animated: chromakey takes them as commands (it has no per-frame expressions).
                        var (similarity, blend) = (Number(Math.Clamp(layer.ChromaSimilarity, 0.01, 1)), Number(Math.Clamp(layer.ChromaBlend, 0, 1)));
                        var keyer = "chromakey";
                        if (context.Commands(effect, "chromakey", ("similarity", "similarity", 0.01, 1), ("blend", "blend", 0, 1)) is { } commands)
                        {
                            if (effect.HasKeys("similarity"))
                                similarity = context.Initial(effect, "similarity", 0.01, 1);
                            if (effect.HasKeys("blend"))
                                blend = context.Initial(effect, "blend", 0, 1);
                            keyer = $"chromakey@{context.InstanceName}";
                            chain.Append(',').Append(commands);
                        }

                        chain.Append($",{keyer}=0x{key}:{similarity}:{blend}{gate}");

                        // The key colour's spill on what is left: green or blue, whichever the key is nearer. Taken off the
                        // screen's own channel (5.0-AudioAnd3D, the user's Q2): despill's scales take it off green by default,
                        // which for a blue screen left the blue cast and darkened the greens.
                        if (effect.Get("despill") is >= 0.005 and var despill)
                        {
                            var blue = Convert.ToInt32(key[4..6], 16) > Convert.ToInt32(key[2..4], 16);
                            chain.Append($",despill=type={(blue ? "blue" : "green")}:mix={Number(Math.Clamp(despill, 0, 1))}:expand=0{(blue ? ":green=0:blue=-1" : "")}{gate}");
                        }

                        // Its Advanced Choke and Smooth (5.0-Release), on its transparency: Matte Choke's passes of erosion or
                        // dilation, and Matte Smooth's Hann weights laid out in space (PSMatteChoke and PSMatteSmooth draw the same).
                        var timed = effect.Range is { } stretch ? $"enable='between(t,{Number(clipStart + stretch.Start)},{Number(clipStart + stretch.End)})'" : null;
                        var choke = Math.Clamp(effect.Get("choke"), -20, 20);
                        if (Math.Abs(choke) >= 0.5)
                        {
                            var passes = Math.Max((int)Math.Round(Math.Abs(choke) * context.PixelScale), 1);
                            run.Add(string.Join(",", Enumerable.Repeat(choke > 0 ? "erosion" : "dilation", passes).Select(f => timed is null ? f : WithOption(f, timed))));
                        }

                        if ((int)Math.Round(Math.Clamp(effect.Get("smooth"), 0, 20) * context.PixelScale) is >= 1 and var smooth)
                            run.Add(string.Join(",", HannSmooth(smooth).Select(f => timed is null ? f : WithOption(f, timed))));
                        break;
                    }

                    case EffectRegistry.Mask or EffectRegistry.Edges when !maskPlaced:
                        PlaceMasks();
                        break;

                    default:
                        if (definition.AlphaOnly)
                        {
                            var filters = new List<(string Filter, bool Geometric)>();
                            AppendEffect(filters, effect, EffectZone.KeyAndMatte, context);
                            if (filters.Count > 0)
                                run.Add(string.Join(",", filters.Select(f => f.Filter)));
                        }

                        break;
                }
            }

            Flush();
            Format();
        }

        /// <summary>
        /// A grey picture (a transparency) smoothed over <paramref name="radius"/> pixels either side with Hann weights,
        /// sin^2(pi k / (2r + 2)) for k = 1 .. 2r + 1 (Matte Smooth's, in space): a row, then a column, of convolution, its
        /// weights in thousandths (convolution takes whole numbers) over their sum. PSMatteSmooth is the same in float.
        /// </summary>
        private static string[] HannSmooth(int radius)
        {
            radius = Math.Clamp(radius, 1, 24);
            var weights = Enumerable.Range(1, 2 * radius + 1).Select(k => (int)Math.Round(1000 * Math.Pow(Math.Sin(Math.PI * k / (2 * radius + 2)), 2))).ToArray();
            var matrix = string.Join(" ", weights);
            var divide = (1.0 / weights.Sum()).ToString("0.############", CultureInfo.InvariantCulture);
            return [$"convolution=0m='{matrix}':0rdiv={divide}:0mode=row", $"convolution=0m='{matrix}':0rdiv={divide}:0mode=column"];
        }

        /// <summary>
        /// How a still grey picture that a transparency is multiplied by (a mask, the edges' shape) ends: one frame, made once.
        /// blend holds it for every frame of the picture it works on (its framesync repeats a last frame, by default) and
        /// ends when that picture does (<see cref="MultiplyByStill"/>).
        ///
        /// It was an endless repeat of that frame (trim, loop=loop=-1) blended with shortest=1, until 4.4-BugHunt: when the
        /// picture ended without having had a frame (mpv's paused sound settle at the end of the file sends the same exact
        /// seek again with the new graph, and a seek past the last frame gives none), blend's framesync took the picture's
        /// end for no time at all, kept taking the endless repeat's frames to catch up with it, and could never output one:
        /// mpv's core thread spun at a whole core in libavfilter (framesync.c), and the player froze for good. One frame
        /// cannot spin: once both have ended, blend ends.
        /// </summary>
        private const string StillMask = ",trim=end_frame=1";

        /// <summary>The step that multiplies the transparency by a still grey picture ended by <see cref="StillMask"/>, labelled <paramref name="mask"/>.</summary>
        private static string MultiplyByStill(string mask) => $"[{mask}]blend=all_mode=multiply";

        /// <summary>
        /// Writes one layer into the graph as a finished picture with its transparency, labelled [name_layer]: cut from the main video
        /// (or the main video itself), or read from its image or video file, and scaled. Its own filters are then
        /// applied to it. What shows of it is the product of everything that makes parts of it transparent: its
        /// own transparency (a PNG's, or a color keyed out), a custom mask from a file, and the rounded or
        /// feathered outline. Opacity scales whatever results, and last of all the picture is turned, and, when
        /// its size is animated, scaled frame by frame.
        /// </summary>
        /// <param name="width">The size it is built at: its size on the frame, or the largest it gets when that is animated.</param>
        /// <param name="mainSource">For the main video: the chain that brings its picture, already scaled.</param>
        private void AppendLayer(StringBuilder chain, Layer layer, string name, string s, int width, int height, double startSeconds, Motion motion, string? mainSource = null,
            List<PictureFade>? fades = null)
        {
            if (mainSource is not null)
            {
                chain.Append(mainSource);
            }
            else if (layer.IsVideoFile)
            {
                // Cut by its crop (every clip's crop reframes its picture: 4.4-BugHunt; a clip of the main video laid as a file
                // by its own, 4.4) before it is scaled to its box; a frame-filling copy covers the frame and is blurred.
                AppendVideoFile(chain, layer, startSeconds);
                if (FileCropOf(layer) is { } cut)
                    chain.Append(',').Append(cut);
                chain.Append(T.BlurTwins.Contains(layer) ? TwinBlur(width, height) : $",scale={width}:{height}");
            }
            else if (layer.IsImage)
            {
                // A single frame, which the overlay repeats for as long as the video runs; cut by its crop, then scaled.
                chain.Append($"movie='{EscapeFilterPath(layer.ImagePath)}'");
                if (FileCropOf(layer) is { } cut)
                    chain.Append(',').Append(cut);
                chain.Append($",scale={width}:{height}");
            }
            else if (layer.IsText)
            {
                // Cropped, its words are drawn at the size the whole of them has at this box's scale, and the crop is cut
                // from that to the pixel: the box is what is left, and the letters stay sharp (no picture scaled up).
                var (left, top, right, bottom) = layer.CropFractions;
                var (wholeWidth, wholeHeight) = (Math.Max((int)Math.Round(width / (1 - left - right)), width), Math.Max((int)Math.Round(height / (1 - top - bottom)), height));
                AppendTextSource(chain, layer, wholeWidth, wholeHeight);
                if ((wholeWidth, wholeHeight) != (width, height))
                {
                    var (cutX, cutY) = (Math.Min((int)Math.Round(wholeWidth * left), wholeWidth - width), Math.Min((int)Math.Round(wholeHeight * top), wholeHeight - height));
                    chain.Append(string.Create(CultureInfo.InvariantCulture, $",crop={width}:{height}:{cutX}:{cutY}"));
                }
            }
            else
            {
                // Cut out of the source by fractions of the frame, so the same layer works at any source size: its source
                // rectangle, less its crop (4.4-BugHunt).
                // (Never cut to nothing: see EvenCut. Where its corner and size run past the picture, crop moves the corner in.)
                var (sourceX, sourceY, sourceWidth, sourceHeight) = layer.CroppedSource;
                chain.Append($"[{name}_in{s}]crop={EvenCut("iw", sourceWidth, SourceWidth)}:{EvenCut("ih", sourceHeight, SourceHeight)}:"
                             + $"iw*{Fraction(sourceX)}:ih*{Fraction(sourceY)},scale={width}:{height}");
            }

            // Zone 2, the Look effects, in stack order, on the clip's clock: keys count from its start (the primary chain's
            // from its first clip's; 4.2.0 counted the main video's from the sequence's, which the view model now converts).
            var clipStart = layer.StartTime - startSeconds;
            var own = BuildLookFilters(layer, width, height, clipStart);
            if (own.Count > 0 && layer.IsStill)
            {
                // The colour filters know nothing of transparency: a picture's own is set aside and put back after them,
                // the flips done to it as well, the whole zone at once.
                chain.Append($",format=yuva420p,split[{name}_fc{s}][{name}_fa_in{s}];[{name}_fa_in{s}]alphaextract"
                             + $"{string.Concat(own.Where(f => f.Geometric).Select(f => "," + f.Filter))}[{name}_fa{s}];"
                             + $"[{name}_fc{s}]{string.Join(",", own.Select(f => f.Filter))}[{name}_fd{s}];[{name}_fd{s}][{name}_fa{s}]alphamerge");
            }
            else if (own.Count > 0)
            {
                chain.Append(',').Append(string.Join(",", own.Select(f => f.Filter)));
            }

            // Zone 3, the key and the matte, in stack order; the picture has its transparency from here on.
            AppendKeyAndMatte(chain, layer, name, s, width, height, clipStart);

            var opacity = Math.Clamp(layer.Opacity, 0, 100) / 100.0;
            if (opacity < 1 && motion.Opacity is null)
                chain.Append($",lutyuv=a=val*{Number(opacity)}");

            // A still picture is one frame, and one frame cannot change shape as time passes: it is made into a
            // frame for every tick first, repeated at the canvas's rate.
            if (layer.IsStill && motion.Reshapes)
                chain.Append($",loop=loop=-1:size=1,setpts=N/({CanvasRate}*TB)");

            // Fading by keyframes: only the transparency is worked out again for each frame, on the frame's own time.
            if (motion.Opacity is { } fade)
            {
                chain.Append($",format=yuva420p,split[{name}_op{s}][{name}_opa_in{s}];[{name}_opa_in{s}]alphaextract,geq=lum='lum(X,Y)*clip({fade},0,1)'[{name}_opa{s}];"
                             + $"[{name}_op{s}][{name}_opa{s}]alphamerge");
            }

            // Turned about its middle, into a box large enough for its corners; what the box adds is transparent.
            var (boxWidth, boxHeight) = GetBox(layer, motion, width, height);
            if (motion.Angle is not null)
                chain.Append($",rotate='({motion.Angle})*PI/180':ow={boxWidth}:oh={boxHeight}:c=black@0");
            else if ((boxWidth, boxHeight) != (width, height) || (layer.HasFilters && Math.Abs(layer.Rotation % 360) >= 0.05))
                chain.Append($",rotate={Number(layer.Rotation)}*PI/180:ow={boxWidth}:oh={boxHeight}:c=black@0");

            // Its fades' transparency (dissolve, wipe, blur), along the frame's own axes, before any size that changes by
            // the frame: every filter in the chain keeps the size it was made for.
            if (fades is { Count: > 0 })
                AppendPictureFades(chain, name, s, fades, boxWidth, boxHeight);

            // An animated size: the finished picture, box and all, scaled for each frame from the size it was built at.
            if (motion.Width is not null)
            {
                var across = boxWidth == width ? motion.Width : $"({motion.Width})*{Ratio((double)boxWidth / width)}";
                chain.Append($",scale=w='max(2,trunc({across}))':h='max(2,trunc(({across})*{Ratio((double)boxHeight / boxWidth)}))':eval=frame");
            }

            chain.Append($"[{name}_layer{s}]");
        }

        private static string Ratio(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        // How the video layer last written by AppendVideoFile ends, for its overlay.
        private string _videoEnding = RepeatingEnding;

        /// <summary>
        /// A video layer's picture, read by a source filter inside the graph (so no second -i is needed), on the
        /// clock of the stretch being composed: its first moment (MediaOffset into its file) lands where the clip
        /// begins.
        ///
        /// A clip with a length, from a file whose start is known, is read from where it is first wanted: the file
        /// is opened at the I-frame before that moment (seek_point), cut exactly there and at the clip's end on the
        /// file's own time stamps, and moved to its place. Nothing before that moment is decoded and nothing after
        /// the clip's end: its decoder stops there, and the overlay lets the picture under it through. The moment
        /// is the start of the stretch.
        ///
        /// A clip with no length repeats without end from the file's first frame, counted on an even clock, as
        /// layers always were; so is a clip from a file not yet inspected.
        /// At a speed other than 1 the clip covers its file that many times as fast: how far into the file it is, is
        /// worked out in the file's own seconds, and its frames are then given the timeline's.
        /// </summary>
        // How far before the moment wanted a video layer's file is asked to seek to, in seconds.
        private const double SeekLead = 1;

        private void AppendVideoFile(StringBuilder chain, Layer layer, double startSeconds)
        {
            var file = layer.ImagePath.Trim().Trim('"');
            var path = EscapeFilterPath(file);
            var speed = GetSpeed(layer);
            var retimed = IsRetimed(speed);
            var known = S.MediaStarts.TryGetValue(file, out var origin);

            if (layer.Duration > 0.001 && known)
            {
                // How far into the file it is read from, and where on this stretch's clock its first moment falls.
                var into = layer.MediaOffset + Math.Max(startSeconds - layer.StartTime, 0) * speed;
                var lead = layer.StartTime - startSeconds;
                chain.Append($"movie='{path}'");

                // The seek lands on the I-frame at or before the point asked for (movie= seeks backwards), and the cut
                // that follows is exact. It is asked a second early: a file with no index (MPEG-TS) may land on a frame
                // that is not an I-frame, and the frames up to the next one would be lost.
                var cutFrom = into;
                if (cutFrom - SeekLead > 0.01)
                    chain.Append(":sp=").Append(Number(cutFrom - SeekLead));

                chain.Append(",trim=");
                if (cutFrom > 0.001)
                    chain.Append("start=").Append(Number(origin + cutFrom)).Append(':');
                chain.Append("end=").Append(Number(origin + layer.MediaOffset + layer.Duration * speed));

                // The file's moment f is shown at lead + (f - origin - MediaOffset) / speed.
                var shift = origin + layer.MediaOffset;
                chain.Append(retimed
                    ? $",setpts=(PTS-{Number(shift)}/TB)/{Ratio(speed)}{Signed(lead)}/TB"
                    : $",setpts=PTS{Signed(lead - shift)}/TB");
                _videoEnding = ":eof_action=pass";
                return;
            }

            // Repeating without end, given an even clock, and started as far in as this stretch is.
            chain.Append($"movie='{path}':loop=0,setpts=N/FRAME_RATE/TB");
            var from = (startSeconds - layer.StartTime) * speed + layer.MediaOffset;
            if (from > 0.01)
                chain.Append($",trim=start={Number(from)},setpts=PTS-STARTPTS{(retimed ? "," + BuildRetime(speed) : "")}");
            else if (from < -0.01)
                chain.Append(retimed ? $",{BuildRetime(speed)}+{Number(-from / speed)}/TB" : $",setpts=PTS+{Number(-from)}/TB");
            else if (retimed)
                chain.Append(',').Append(BuildRetime(speed));
            _videoEnding = RepeatingEnding;
        }

        /// <summary>
        /// How a layer that repeats without end is laid on: it goes when the picture under it does (shortest=1), and takes no
        /// part in deciding when (repeatlast=0: the overlay's framesync waits on the picture under it alone). Without the
        /// second, a picture under it that ended without having had a frame (Live Preview at the end of the file, after the
        /// paused sound settle's seek past the last frame) left the overlay reading the endless layer for ever to catch up with
        /// it: mpv's core thread spinning in libavfilter, the player frozen, as the endless masks did (see StillMask; 4.4-BugHunt).
        /// The pictures are the same frame for frame.
        /// </summary>
        private const string RepeatingEnding = ":shortest=1:repeatlast=0";

        /// <summary>
        /// A text layer's picture: its words drawn by drawtext on a see-through canvas the size the layer is built
        /// at. It is one frame, as an image from a file is, and from here on it is treated as one: masked, faded,
        /// turned, and moved and scaled by its keyframes like any other picture.
        ///
        /// Each line is drawn by a drawtext of its own, centred, with its baseline where the layout puts it
        /// (drawtext places a line by the top of its tallest letter, so that height is taken off again: "y-ascent").
        /// The font is scaled to the canvas, so the words fill the layer's box at whatever size it is built.
        /// The canvas is see-through but has a colour, the outline's or the letters': the soft edge of a letter
        /// fades towards it, and so shows no dark fringe.
        /// </summary>
        private static void AppendTextSource(StringBuilder chain, Layer layer, int width, int height)
        {
            var layout = TextLayout.Measure(layer);
            var scale = Math.Min((double)width / layout.Width, (double)height / layout.Height);
            var border = layer.OutlineThickness > 0 ? Math.Max((int)Math.Round(Math.Clamp(layer.OutlineThickness, 0, TextLayout.LargestOutline) * scale), 1) : 0;
            var (letters, outline) = (TextColor(layer.FontColor, "FFFFFF"), TextColor(layer.OutlineColor, "000000"));
            var font = layout.FontFile.Length > 0 ? $"fontfile='{EscapeFilterPath(layout.FontFile)}'" : $"font={EscapeDrawText(layout.FontName)}";

            chain.Append($"color=c=0x{(border > 0 ? outline : letters)}@0:s={width}x{height}:r=1,format=yuva444p");

            // The lines are in the middle of the canvas, top to bottom, as they are in the middle of the layout.
            var top = (height - layout.Lines.Count * layout.LineHeight * scale) / 2;
            for (var i = 0; i < layout.Lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(layout.Lines[i]))
                    continue;

                var baseline = top + (i * layout.LineHeight + layout.Baseline) * scale;
                chain.Append($",drawtext={font}:text={EscapeDrawText(layout.Lines[i])}:expansion=none"
                             + $":fontsize={Number(Math.Max(layer.FontSize * scale, 1))}:fontcolor=0x{letters}");
                if (border > 0)
                    chain.Append($":bordercolor=0x{outline}:borderw={border}");
                chain.Append($":x=(w-text_w)/2:y={Number(baseline)}-ascent");
            }

            chain.Append(",trim=end_frame=1");
        }

        /// <summary>#RRGGBB as FFmpeg writes a color, RRGGBB; anything unreadable becomes the fallback.</summary>
        private static string TextColor(string? color, string fallback)
        {
            var hex = (color ?? "").Trim().TrimStart('#');
            return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : fallback;
        }

        /// <summary>
        /// Words as the value of a drawtext option, quoted, for a filter graph that is itself inside a command.
        /// The words pass through three readers, and each has characters of its own to be kept from:
        ///
        ///   drawtext itself   would expand "%{...}" and "\"; it is told not to (expansion=none), so nothing here.
        ///   the option list   ends a value at ":" and reads "\" and "'" itself: each gets a backslash.
        ///   the filter graph  reads "'", "\", ",", ";", "[" and "]": the value is put in single quotes, where
        ///                     all of them are plain, and a quote inside it is written by closing the quotes,
        ///                     escaping one, and opening them again.
        ///
        /// Outside all of those, the graph sits in double quotes on a command line, where a double quote cannot be
        /// written at all without the command being read differently by everything that reads it. A straight
        /// double quote is therefore drawn as a typographic one, opening or closing by where it stands.
        /// Line breaks and other control characters never get here: a line is one drawtext.
        /// </summary>
        private static string EscapeDrawText(string text)
        {
            var value = new StringBuilder(text.Length + 16).Append('\'');
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\\': value.Append(@"\\"); break;
                    case ':': value.Append(@"\:"); break;
                    case '\'': value.Append(@"\'\''"); break;
                    case '"': value.Append(i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is '(' or '[' or '{' ? '\u201C' : '\u201D'); break;
                    default:
                        if (!char.IsControl(c))
                            value.Append(c);
                        break;
                }
            }

            return value.Append('\'').ToString();
        }

        /// <summary>#RRGGBB as FFmpeg writes a color, RRGGBB; anything unreadable becomes green.</summary>
        private static string KeyColor(string color)
        {
            var hex = (color ?? "").Trim().TrimStart('#');
            return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : "00FF00";
        }

        /// <summary>
        /// The geq expression (0 to 1 per pixel) for a layer's shape, or null when it is a plain rectangle.
        /// It measures how far a pixel is inside a rounded rectangle: zero on the edge, rising inwards. Rounded
        /// corners cut the picture off at that edge; a feather lets it fade in over its width instead.
        /// </summary>
        private static string? BuildLayerMask(Layer element, int width, int height, double pixelScale = 1, bool softEdge = true)
        {
            // The edges card switched off (or every effect bypassed): a plain rectangle.
            if (!element.FxOn(EffectRegistry.Edges))
                return null;

            var limit = Math.Max(Math.Min(width, height) / 2 - 1, 1);
            var corner = Math.Min((int)Math.Round(Math.Clamp(element.CornerRadius, 0, 50) / 100.0 * Math.Min(width, height)), limit);
            var feather = softEdge && element.Feather ? Math.Clamp((int)Math.Round(element.FeatherRadius * pixelScale), 1, limit) : 0;
            if (corner == 0 && feather == 0)
                return null;

            // A feathered edge is at least as round as it is soft; without a feather the edge is one pixel of anti-aliasing.
            var radius = Math.Max(corner, feather);
            var ramp = Math.Max(feather, 1);
            return $"clip(({radius}.5-hypot(max({radius}-X,0)+max(X-(W-1-{radius}),0),max({radius}-Y,0)+max(Y-(H-1-{radius}),0)))/{ramp},0,1)";
        }

        /// <summary>
        /// A layer's mask as a grey picture the size of the layer, white where it shows, from its file: for a mask of the
        /// whole frame, the part under the layer's cut-out, cut as the picture is (its source rectangle less its crop); for a
        /// mask of the layer's own picture, the part its crop leaves (the main video's clips too, by the crop of their stream), so that it stays where it was on the picture (4.4-BugHunt:
        /// the crop reframes the picture, it is no mask). Then grown or shrunk, turned inside out, nudged, and its outline
        /// softened. It is one picture, worked out once: none of this is done again for each frame.
        /// </summary>
        /// <param name="fromAlpha">The picture is read by its transparency: painted shows, see-through is cut away.</param>
        /// <param name="sourceSize">The main video's size, which a mask of the whole frame is drawn at (see <see cref="MaskCut"/>).</param>
        /// <param name="pictureCrop">The crop the layer's picture is cut by (<see cref="PictureCropOf"/>).</param>
        private static string BuildMaskSource(Layer layer, string file, bool fromAlpha, int width, int height, double pixelScale, (int Width, int Height) sourceSize,
            (double Left, double Top, double Right, double Bottom) pictureCrop)
        {
            var source = new StringBuilder();
            source.Append($"movie='{EscapeFilterPath(file)}',{MaskReader(fromAlpha)}");
            if (layer.UsesFrameMask)
            {
                var (sourceX, sourceY, sourceWidth, sourceHeight) = layer.CroppedSource;
                source.Append($",crop={MaskCut("iw", sourceWidth, sourceSize.Width)}:{MaskCut("ih", sourceHeight, sourceSize.Height)}:iw*{Fraction(sourceX)}:ih*{Fraction(sourceY)}");
            }
            else if (pictureCrop is var (left, top, right, bottom) && left + top + right + bottom > 0)
            {
                // (Its file's size is not known here: each side is held to a pixel at least.)
                source.Append($",crop={MaskCut("iw", 1 - left - right, 0)}:{MaskCut("ih", 1 - top - bottom, 0)}:iw*{Fraction(left)}:ih*{Fraction(top)}");
            }

            source.Append($",scale={width}:{height}");
            if (layer.MaskInvert)
                source.Append(",geq=lum='255-lum(X,Y)'");

            // Grown or shrunk by so many pixels: one pass of dilation (or erosion) moves the edge out (or in) by one.
            var grow = layer.MaskGrow == 0 ? 0 : Math.Max((int)Math.Round(Math.Abs(Math.Clamp(layer.MaskGrow, -Layer.MaskGrowLimit, Layer.MaskGrowLimit)) * pixelScale), 1);
            for (var pass = 0; pass < grow; pass++)
                source.Append(layer.MaskGrow > 0 ? ",dilation" : ",erosion");

            // Nudged (Mask Offset): the picture moved by so many pixels inside the layer (at most half of it either
            // way), and what it uncovers at the far edge black, so hidden. A geq, not pad: pad's black on a grey
            // picture can be video black (16), which would let the layer show faintly there.
            var (dx, dy) = ((int)Math.Round(Layer.MaskOffset(layer.MaskOffsetX) / 100.0 * width), (int)Math.Round(Layer.MaskOffset(layer.MaskOffsetY) / 100.0 * height));
            if (dx != 0 || dy != 0)
                source.Append($",geq=lum='lum(X{Signed(-dx)},Y{Signed(-dy)})*between(X,{Number(dx)},{Number(width - 1 + dx)})*between(Y,{Number(dy)},{Number(height - 1 + dy)})'");

            // Soft edges: the mask's own outline fades out over the Soft Edge width. A box blur run twice, as wide as a
            // Gaussian of half the Soft Edge would be: gblur takes libmpv down when its result is blended into a layer's
            // transparency (the export's FFmpeg does not mind, but the player must run the same graph).
            if (layer.Feather && layer.FxOn(EffectRegistry.Edges) && BoxRadius(Math.Clamp(layer.FeatherRadius, 1, 100) * pixelScale / 2, width, height) is > 0 and var radius)
                source.Append($",boxblur=luma_radius={radius}:luma_power=2");
            return source.ToString();
        }

        /// <summary>
        /// The radius of a box blur that, run twice, spreads as far as a Gaussian of the given sigma: two passes of
        /// a box of radius r have a variance of ((2r + 1)^2 - 1) / 6. Held to what boxblur accepts for a picture of
        /// this size (no more than half its shorter side); 0 for no blur at all.
        /// </summary>
        private static int BoxRadius(double sigma, int width, int height)
        {
            var radius = (int)Math.Round((Math.Sqrt(6 * sigma * sigma + 1) - 1) / 2);
            return Math.Clamp(radius, 0, Math.Max(Math.Min(width, height) / 2 - 1, 0));
        }

        /// <summary>How a mask picture is turned into grey: its transparency, or its brightness.</summary>
        private static string MaskReader(bool fromAlpha) => fromAlpha ? "format=rgba,alphaextract" : "format=gray";

        /// <summary>
        /// One side of the part of a mask of the whole frame under a piece: iw*F, as it always was; held to a pixel at least
        /// (crop refuses nothing, and takes the graph down with it) where the source's size, which such a mask is drawn at,
        /// leaves under 8 pixels of it, so that one drawn a few times smaller than the source is never cut to nothing either.
        /// The fraction is at most 1: never more than the picture.
        /// </summary>
        private static string MaskCut(string side, double fraction, int known)
        {
            var written = Fraction(fraction);
            return known <= 0 || known * double.Parse(written, CultureInfo.InvariantCulture) < 8 ? $"'max(1,{side}*{written})'" : $"{side}*{written}";
        }

        private static string Fraction(double value) => Math.Clamp(value, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture);

        /// <summary>What is applied to the finished picture, once: the fades over everything.</summary>
        private List<string> BuildWholeVideoFilters(double duration)
        {
            var filters = new List<string>();

            if (FadeIn)
                filters.Add($"fade=t=in:st=0:d={Number(FadeSeconds)}");
            if (FadeOut && duration > FadeSeconds)
                filters.Add($"fade=t=out:st={Number(duration - FadeSeconds)}:d={Number(FadeSeconds)}");

            return filters;
        }
        /// <summary>One subtitles filter per track that is to be burned into the picture.</summary>
        private List<string> BuildSubtitleBurnFilters(string sourcePath)
        {
            var escaped = EscapeFilterPath(sourcePath);
            return S.BurnedSubtitleStreams.Select(index => $"subtitles='{escaped}':si={index}").ToList();
        }

        // ----- Motion as FFmpeg expressions -----

        /// <summary>
        /// A property's keyframes as an expression of t, the time on the picture being composed: the first value,
        /// plus for every stretch between two keyframes its change times how far through that stretch t is, held
        /// to 0..1 and bent into the easing of the keyframe the stretch leaves. That is the path from each
        /// keyframe to the next, level before the first and after the last, in one flat sum: one term for each
        /// stretch that changes anything, and none for one that does not.
        /// </summary>
        /// <param name="clipStart">Where the clip begins on that picture's clock, in seconds.</param>
        /// <param name="scale">What a value is multiplied by: the frame's width or height in pixels, or 1.</param>
        /// <param name="time">What the clock is called where the expression is used: t in most filters, T in geq.</param>
        private static string KeyExpression(IReadOnlyList<Keyframe> keys, double clipStart, double scale, string time = "t")
        {
            var text = new StringBuilder(16 + keys.Count * 36);
            text.Append(Number(keys[0].Value * scale));
            for (var i = 1; i < keys.Count; i++)
            {
                var change = (keys[i].Value - keys[i - 1].Value) * scale;
                if (Math.Abs(change) < 0.0005)
                    continue;

                var (from, length) = (clipStart + keys[i - 1].Time, keys[i].Time - keys[i - 1].Time);
                var easing = keys[i - 1].Easing;

                // Two keyframes at the same moment are a jump; a Hold is one at the next keyframe.
                if (length < 0.001 || easing == EasingType.Hold)
                {
                    text.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)));
                    text.Append($"*gte({time},").Append(Number(easing == EasingType.Hold ? from + length : from)).Append(')');
                }

                // A curve with no closed form (Bounce, Custom Bézier): one clipped ramp for each of its straight pieces,
                // every one a term of the same flat sum. A piece that changes nothing adds nothing.
                else if (EasingCurves.IsPieced(easing))
                {
                    var points = EasingCurves.PiecesOf(keys[i - 1]);
                    for (var k = 1; k < points.Length; k++)
                    {
                        var part = change * (points[k].E - points[k - 1].E);
                        if (Math.Abs(part) < 0.0005)
                            continue;

                        var (pieceFrom, pieceLength) = (from + points[k - 1].U * length, (points[k].U - points[k - 1].U) * length);
                        text.Append(part < 0 ? '-' : '+').Append(Number(Math.Abs(part)));
                        if (pieceLength < 0.001)
                            text.Append($"*gte({time},").Append(Number(pieceFrom)).Append(')');
                        else
                            text.Append('*').Append(Progress(time, pieceFrom, pieceLength));
                    }
                }
                else
                {
                    text.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)));
                    text.Append('*').Append(EasingCurves.Expression(Progress(time, from, length), keys[i - 1]));
                }
            }

            return text.ToString();
        }

        /// <summary>How far through a stretch the clock is, held to 0..1: clip((t-from)/length,0,1).</summary>
        private static string Progress(string time, double from, double length) =>
            $"clip(({time}{(from < 0 ? '+' : '-')}{Number(Math.Abs(from))})/{Ratio(length)},0,1)";

        /// <summary>How a layer moves while a stretch of the picture is composed; every part is null for a layer that keeps still in that respect.</summary>
        /// <param name="X">Its left edge in pixels, as an expression.</param>
        /// <param name="Width">Its width in pixels, as an expression.</param>
        /// <param name="Angle">Degrees it is turned, as an expression.</param>
        /// <param name="LargestWidth">The widest it gets, as a fraction of the frame: what it is built at, so that it is only ever scaled down.</param>
        /// <param name="Opacity">How solid it is, 0 to 1, as an expression of the frame's time (T, for geq).</param>
        /// <param name="ShiftX">What its slides add to where it is laid across, in pixels, as an expression (see WithTransitions).</param>
        /// <param name="Fades">Whether its picture fades in or out somewhere in the stretch (dissolve, wipe, blur): it changes from frame to frame.</param>
        private readonly record struct Motion(string? X, string? Y, string? Width, string? Angle, double LargestWidth, string? Opacity = null,
            string? ShiftX = null, string? ShiftY = null, bool Fades = false)
        {
            public bool Any => X is not null || Y is not null || Width is not null || Angle is not null || Opacity is not null || ShiftX is not null || ShiftY is not null;

            /// <summary>Whether the picture itself changes from frame to frame, not only where it is laid.</summary>
            public bool Reshapes => Width is not null || Angle is not null || Opacity is not null || Fades;
        }

        // ----- Fades and transitions of a clip's picture -----
        // Every clip fades on its own (Layer.FadeInSeconds and the rest; the main video's clips' from MainClip), over
        // whatever the stack has under it. How its picture comes on or goes is its style: a dissolve and a blur dissolve
        // are its transparency (fade, cheap; geq on the alpha where a fade began before the stretch), a wipe is its
        // transparency cut behind a moving soft edge (geq on the alpha, only while it wipes), a slide is where it is laid,
        // a zoom its size about its anchor (the motion's expressions, so they work with its keyframes). Each is at work
        // only inside its fade, so a clip with fades costs nothing more outside them.

        /// <summary>A fade of a picture, on the clock of the stretch being composed: from Start, Length seconds, coming on (In) or going, in a style.</summary>
        private readonly record struct PictureFade(double Start, double Length, bool In, TransitionStyle Style)
        {
            public double End => Start + Length;

            /// <summary>While it is at work, as a timeline expression: from its start (inclusive) to its end (not).</summary>
            public string Gate(string time = "t") => $"(gte({time},{Number(Start)})*lt({time},{Number(End)}))";

            /// <summary>How much of the picture is there through it, 0 to 1, as an expression of the time, in its style's curve.</summary>
            public string Shown(string time = "t")
            {
                var progress = Progress(time, Start, Length);
                return EasingCurves.Expression(In ? progress : $"(1-{progress})", TransitionStyles.Easing(Style));
            }
        }

        /// <summary>
        /// The fades of a layer's picture in this stretch: its own two, the main video's those of each of its clips. None for
        /// a sound, an adjustment layer's (see AppendAdjustment) or a fade a crossfade has given to the clip over it
        /// (PictureFadeInOff).
        /// </summary>
        private List<PictureFade> PictureFades(Layer layer, double from)
        {
            var fades = new List<PictureFade>();
            void Add(double start, double length, bool fadeIn, TransitionStyle style)
            {
                if (length > 0.01)
                    fades.Add(new PictureFade(start - from, length, fadeIn, style));
            }

            if (layer.IsMainVideo)
            {
                foreach (var clip in T.CenterClips)
                {
                    var length = clip.End - clip.Start;
                    Add(clip.Start, Math.Min(clip.FadeIn, length), true, clip.FadeInStyle);
                    var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - clip.FadeIn, 0));
                    Add(clip.End - fadeOut, fadeOut, false, clip.FadeOutStyle);
                }
            }
            else if (layer.HasTiming && !layer.IsAudio)
            {
                var (start, end) = layer.GetSpan(SequenceSeconds);
                var length = end - start;
                if (!layer.PictureFadeInOff)
                    Add(start, Math.Min(layer.FadeInSeconds, length), true, layer.FadeInStyle);
                var fadeOut = Math.Min(layer.FadeOutSeconds, Math.Max(length - (layer.PictureFadeInOff ? 0 : layer.FadeInSeconds), 0));
                if (!layer.PictureFadeOutOff)
                    Add(end - fadeOut, fadeOut, false, layer.FadeOutStyle);
            }

            return fades;
        }

        /// <summary>
        /// A layer's motion with what its slides and zooms do to it: a slide adds to where it is laid, from beyond the edge
        /// of the frame it comes from to where it is (or from there to beyond the edge it goes to); a zoom multiplies its
        /// size, about its anchor point, from nothing (with a little overshoot) to all of it. <paramref name="rect"/> is where
        /// the layer is in the frame, in the frame's pixels.
        /// </summary>
        private static Motion WithTransitions(Motion motion, List<PictureFade> fades, (int X, int Y, int Width, int Height) rect, int width, int height)
        {
            if (fades.Count == 0)
                return motion;

            var (shiftX, shiftY, zoom) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
            foreach (var fade in fades)
            {
                var (dx, dy) = TransitionStyles.Direction(fade.Style);
                if (TransitionStyles.IsSlide(fade.Style))
                {
                    // Coming on, it comes from the side it travels away from; going, it goes to the side it travels to.
                    var side = fade.In ? -1 : 1;
                    double Beyond(int toward, int at, int size, int frame) => toward > 0 ? frame - at + 2 : -(at + size) - 2;
                    if (dx != 0)
                        shiftX.Append($"+{Signed2(Beyond(dx * side, rect.X, rect.Width, width))}*(1-{fade.Shown()})*{fade.Gate()}");
                    if (dy != 0)
                        shiftY.Append($"+{Signed2(Beyond(dy * side, rect.Y, rect.Height, height))}*(1-{fade.Shown()})*{fade.Gate()}");
                }
                else if (fade.Style == TransitionStyle.Zoom)
                {
                    zoom.Append($"*(1-(1-{fade.Shown()})*{fade.Gate()})");
                }
            }

            motion = motion with { Fades = true };
            if (shiftX.Length > 0)
                motion = motion with { ShiftX = $"0{shiftX}" };
            if (shiftY.Length > 0)
                motion = motion with { ShiftY = $"0{shiftY}" };
            if (zoom.Length > 0)
            {
                // Built a little larger than it ever is, for the overshoot, and scaled for each frame from there.
                var largest = motion.Width is null ? (double)rect.Width / Math.Max(width, 1) : motion.LargestWidth;
                motion = motion with { Width = $"({motion.Width ?? Number(rect.Width)}){zoom}", LargestWidth = largest * 1.12 };
            }

            return motion;
        }

        private static string Signed2(double value) => value < 0 ? $"({Number(value)})" : Number(value);

        private static bool EndsWith(StringBuilder text, string end)
        {
            if (text.Length < end.Length)
                return false;
            for (var i = 0; i < end.Length; i++)
            {
                if (text[text.Length - end.Length + i] != end[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The fades of a picture that are its transparency (dissolves, blur dissolves, wipes, and the first moments of a
        /// zoom, so that nothing is left of it but a dot): appended to its chain, each at work only inside its fade. A blur
        /// dissolve's blur is an avgblur told its radius a dozen times a second (sendcmd), off outside the fade.
        /// </summary>
        private void AppendPictureFades(StringBuilder chain, string name, string s, List<PictureFade> fades, int width, int height)
        {
            if (fades.Count == 0)
                return;

            if (!EndsWith(chain, "format=yuva420p"))
                chain.Append(",format=yuva420p");

            // Worked out on the transparency set aside: wipes, and fades that began before this stretch (fade cannot
            // start before 0). The geq runs only inside each.
            var onAlpha = new List<(string Factor, PictureFade Fade)>();
            var feather = Math.Max(2, Math.Round(Math.Max(width, height) * 0.02));
            foreach (var fade in fades)
            {
                var (dx, dy) = TransitionStyles.Direction(fade.Style);
                if (TransitionStyles.IsWipe(fade.Style))
                {
                    // What is left of it: the part on the side the edge leaves behind (coming on) or travels towards (going).
                    var q = fade.Shown("T");
                    var keepLowSide = fade.In ? dx + dy > 0 : dx + dy < 0;
                    var (along, size) = dx != 0 ? ("X", "W") : ("Y", "H");
                    var factor = keepLowSide
                        ? $"clip((({size}+{Number(feather)})*{q}-{along})/{Number(feather)},0,1)"
                        : $"clip(({along}-({size}+{Number(feather)})*(1-{q}))/{Number(feather)}+1,0,1)";
                    onAlpha.Add((factor, fade));
                }
                else if (Dissolves(fade) is { } dissolve && dissolve.Start < 0)
                {
                    onAlpha.Add(($"({dissolve.Shown("T")})", dissolve));
                }
            }

            if (onAlpha.Count > 0)
            {
                chain.Append($",split[{name}_tf{s}][{name}_tfa_in{s}];[{name}_tfa_in{s}]alphaextract");
                foreach (var (factor, fade) in onAlpha)
                    chain.Append($",geq=lum='lum(X,Y)*{factor}':enable='between(t,{Number(fade.Start)},{Number(fade.End)})'");
                chain.Append($"[{name}_tfa{s}];[{name}_tf{s}][{name}_tfa{s}]alphamerge");
            }

            foreach (var fade in fades)
            {
                // The blur of a blur dissolve: all of it where the picture is not there yet, none where all of it is.
                if (fade.Style == TransitionStyle.BlurDissolve)
                {
                    var most = Math.Clamp((int)Math.Round(Math.Max(width, height) / 40.0), 2, 64);
                    var instance = $"fx{++_effectInstances}";
                    int RadiusAt(double t) => Math.Max((int)Math.Round(most * (1 - EasingCurves.Evaluate(fade.In ? (t - fade.Start) / fade.Length : (fade.End - t) / fade.Length, default))), 1);
                    var commands = new StringBuilder();
                    var steps = Math.Clamp((int)Math.Ceiling(fade.Length * 12), 1, 720);
                    for (var i = 0; i <= steps; i++)
                    {
                        var t = fade.Start + fade.Length * i / steps;
                        if (t <= 0.0005)
                            continue;
                        var radius = RadiusAt(t).ToString(CultureInfo.InvariantCulture);
                        commands.Append(commands.Length > 0 ? ";" : "").Append($"{Number(t)} avgblur@{instance} sizeX {radius}, avgblur@{instance} sizeY {radius}");
                    }

                    var first = RadiusAt(Math.Max(fade.Start, 0)).ToString(CultureInfo.InvariantCulture);
                    if (commands.Length > 0)
                        chain.Append($",sendcmd=c='{commands}'");
                    chain.Append($",avgblur@{instance}=sizeX={first}:sizeY={first}:enable='between(t,{Number(fade.Start)},{Number(fade.End)})'");
                }

                if (Dissolves(fade) is { Start: >= 0 } dissolve)
                    chain.Append($",fade=t={(dissolve.In ? "in" : "out")}:st={Number(dissolve.Start)}:d={Ratio(dissolve.Length)}:alpha=1:enable='between(t,{Number(dissolve.Start)},{Number(dissolve.End)})'");
            }

            // The transparency of a fade's own: a dissolve, a blur dissolve, and a zoom's first (or last) fifth.
            static PictureFade? Dissolves(PictureFade fade) => fade.Style switch
            {
                TransitionStyle.Dissolve or TransitionStyle.BlurDissolve => fade with { Style = TransitionStyle.Dissolve },
                TransitionStyle.Zoom => fade.In ? fade with { Length = fade.Length / 5, Style = TransitionStyle.Dissolve }
                    : fade with { Start = fade.End - fade.Length / 5, Length = fade.Length / 5, Style = TransitionStyle.Dissolve },
                _ => null,
            };
        }

        /// <param name="from">Where on the sequence the stretch being composed begins: its clock starts there.</param>
        private static Motion GetMotion(Layer layer, int frameWidth, int frameHeight, double from)
        {
            if (!layer.IsAnimated)
                return default;

            var start = layer.StartTime - from;
            string? Of(KeyProperty property, double scale) => layer.HasKeys(property) ? KeyExpression(layer.GetKeys(property), start, scale) : null;
            return new Motion(
                Of(KeyProperty.X, frameWidth), Of(KeyProperty.Y, frameHeight), Of(KeyProperty.Scale, frameWidth), layer.HasFilters ? Of(KeyProperty.Rotation, 1) : null,
                layer.HasKeys(KeyProperty.Scale) ? layer.GetKeys(KeyProperty.Scale).Max(k => k.Value) : 0,
                layer.HasKeys(KeyProperty.Opacity) && layer.IsPicture ? KeyExpression(layer.GetKeys(KeyProperty.Opacity), start, 0.01, "T") : null);
        }


        /// <summary>
        /// The step that puts the captions on the picture as its top layer. They are drawn on a transparent
        /// canvas the size of the caption box, which is then laid over the frame where the box sits; that is
        /// what lets the captions be placed, sized and styled like any other layer: translucent, with rounded
        /// or soft edges (the same mask a picture gets) and a drop shadow (the words' own outline, in black).
        /// </summary>
        private string BuildCaptionOverlay(string assPath)
        {
            var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
            var (x, y, width, height) = CaptionLayer.GetOutputRect(frameWidth, frameHeight, 0, 0);
            (width, height) = (Math.Max(width, 16), Math.Max(height, 16));
            var rate = GetTargetFramerate() ?? Number(SourceFrameRate);
            var opacity = Math.Clamp(CaptionLayer.Opacity, 0, 100) / 100.0;

            var layer = $"color=c=black@0:s={width}x{height}:r={rate},format=rgba,{CaptionFilter(assPath)}:alpha=1";
            if (BuildLayerMask(CaptionLayer, width, height, 1) is { } mask)
                layer += $",format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{mask}{(opacity < 1 ? "*" + Number(opacity) : "")}'";
            else if (opacity < 1)
                layer += $",colorchannelmixer=aa={Number(opacity)}";

            // A mask of its own, as any layer can have: the words show where the mask is white (one frame, held: see StillMask).
            var maskPath = CaptionLayer.MaskPath.Trim().Trim('"');
            if (CaptionLayer.CustomMask && Exists(maskPath))
            {
                layer += $",format=yuva420p,split[cap_pic][cap_a_in];[cap_a_in]alphaextract[cap_a0];"
                         + $"movie='{EscapeFilterPath(maskPath)}',{MaskReader(S.AlphaMasks.Contains(maskPath))},scale={width}:{height}{StillMask}[cap_mask];"
                         + $"[cap_a0]{MultiplyByStill("cap_mask")}[cap_a1];[cap_pic][cap_a1]alphamerge";
            }

            if (!CaptionLayer.Shadow)
                return $"null[cap_base];{layer}[cap_layer];[cap_base][cap_layer]overlay={x}:{y}:shortest=1";

            var (shadowChain, shadowX, shadowY) = ShadowOf(CaptionLayer);
            return $"null[cap_base];{layer},split[cap_layer][cap_shadow_in];"
                   + $"[cap_shadow_in]{shadowChain}[cap_shadow];"
                   + $"[cap_base][cap_shadow]overlay={x + shadowX}:{y + shadowY}:shortest=1[cap_shaded];"
                   + $"[cap_shaded][cap_layer]overlay={x}:{y}:shortest=1";
        }
    }
}
