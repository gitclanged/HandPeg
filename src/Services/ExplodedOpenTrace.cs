using System.Diagnostics;
using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// Measures how a 3D Exploded View opens (Q2, Pass 5.6.2B), in preview-traced builds only (<see cref="PreviewTrace"/>, or
/// HANDPEG_TRACE_3D=1): one log line from the editor and one from the compositor worker per open.
/// <para>Editor: the toggle to the layer shown (SetExplodedView to ShowExplodedLayer), to the first 3D scene handed back, to the
/// <see cref="Frames"/>-th, and the worst and mean cost of the per-frame badge update over those frames.</para>
/// <para>Worker (<see cref="Worker"/>): each of the first <see cref="Frames"/> RenderExploded frames' CPU ms and the time from one
/// frame's start to the next; the first frame split into setup (3D picture, backdrop), layout (sort, plane sizes, rails, canvas frame),
/// planes and the final pass, with the textures it made and the planes drawn.</para>
/// </summary>
public static class ExplodedOpenTrace
{
    /// <summary>Frames measured after the open.</summary>
    public const int Frames = 15;

    private static bool? _on;

    /// <summary>Whether opens are measured in this process (decided once).</summary>
    public static bool On => _on ??= PreviewTrace.IsOn || Environment.GetEnvironmentVariable("HANDPEG_TRACE_3D") == "1";

    // ----- Editor side (UI thread only) -----

    private static long _clicked, _shown, _firstScene;
    private static int _scenes;
    private static double _worstUi, _sumUi;

    /// <summary>The 3D view was asked for (a click, a key): the clock starts.</summary>
    public static void Asked()
    {
        if (!On)
            return;
        (_clicked, _shown, _firstScene, _scenes, _worstUi, _sumUi) = (Stopwatch.GetTimestamp(), 0, 0, 0, 0, 0);
    }

    /// <summary>The 3D layer (panel, badges, input) is on screen.</summary>
    public static void LayerShown()
    {
        if (On && _clicked != 0)
            _shown = Stopwatch.GetTimestamp();
    }

    /// <summary>One per-frame update of the editor while 3D opens: <paramref name="scene"/> when a new 3D scene came back.</summary>
    public static void UiFrame(long began, bool scene)
    {
        if (!On || _clicked == 0 || !scene)
            return;

        var now = Stopwatch.GetTimestamp();
        var ms = Stopwatch.GetElapsedTime(began, now).TotalMilliseconds;
        if (_scenes++ == 0)
            _firstScene = now;
        (_worstUi, _sumUi) = (Math.Max(_worstUi, ms), _sumUi + ms);
        if (_scenes < Frames)
            return;

        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"3D open (editor): layer shown {Ms(_clicked, _shown)} ms, first 3D scene {Ms(_clicked, _firstScene)} ms, {Frames}th {Ms(_clicked, now)} ms; badge update worst {_worstUi:0.00} ms, mean {_sumUi / _scenes:0.00} ms"));
        _clicked = 0;
    }

    private static string Ms(long from, long to) =>
        from == 0 || to == 0 ? "-" : Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Worker side: one per renderer; a frame after a flat one starts a new open.</summary>
    public sealed class Worker
    {
        private readonly double[] _cpu = new double[Frames], _between = new double[Frames];
        private long _frameStart, _setup, _layout, _planes;
        private int _frame = -1, _texturesBefore, _texturesMade, _planeCount, _keptCount;
        private bool _flat = true;

        /// <summary>A flat frame was drawn: the next 3D frame opens the view.</summary>
        public void Flat() => _flat = true;

        /// <summary>RenderExploded started.</summary>
        public void Begin(long at, int texturesMade)
        {
            if (_flat)
            {
                (_flat, _frame, _texturesBefore) = (false, 0, texturesMade);
            }
            else if (_frame is >= 1 and < Frames)
            {
                // From the previous frame's start to this one's (frame k's start, k = _frame).
                _between[_frame - 1] = Stopwatch.GetElapsedTime(_frameStart, at).TotalMilliseconds;
            }

            _frameStart = at;
        }

        /// <summary>First frame only: the 3D picture and backdrop ready.</summary>
        public void Setup()
        {
            if (_frame == 0)
                _setup = Stopwatch.GetTimestamp();
        }

        /// <summary>First frame only: planes sorted and sized, rails and canvas frame drawn.</summary>
        public void Laid(int planes, int kept)
        {
            if (_frame == 0)
                (_layout, _planeCount, _keptCount) = (Stopwatch.GetTimestamp(), planes, kept);
        }

        /// <summary>First frame only: every plane drawn.</summary>
        public void PlanesDrawn()
        {
            if (_frame == 0)
                _planes = Stopwatch.GetTimestamp();
        }

        /// <summary>RenderExploded ended (after the final pass); logs once <see cref="Frames"/> frames are in, with the GPU clock.</summary>
        public void End(int texturesMade, float gpuMs)
        {
            if (_frame is < 0 or >= Frames)
                return;

            var end = Stopwatch.GetTimestamp();
            _cpu[_frame] = Stopwatch.GetElapsedTime(_frameStart, end).TotalMilliseconds;
            if (_frame == 0)
            {
                _texturesMade = texturesMade - _texturesBefore;
                AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                    $"3D open (worker) frame 1: {_cpu[0]:0.00} ms CPU = setup {Ms(_frameStart, _setup)} + layout {Ms(_setup, _layout)} + planes {Ms(_layout, _planes)} ({_planeCount} planes, {_keptCount} cached) + final {Ms(_planes, end)}; {_texturesMade} textures made"));
            }

            if (++_frame < Frames)
                return;

            AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"3D open (worker) {Frames} frames: CPU ms [{string.Join(" ", _cpu.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))}], frame-to-frame ms [{string.Join(" ", _between.Take(Frames - 1).Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))}], GPU {gpuMs:0.00} ms (rolling)"));
        }
    }
}
