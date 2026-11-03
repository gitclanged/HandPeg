using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace HandPegApp.Controls;

/// <summary>
/// One tile of the Media pane seen in perspective, tilted toward the pointer with a sheen where it is (5.1-GpuFx, TileTilt.fx). There is one of these, on the tile under the
/// pointer and on no other, so it costs nothing at rest. Off with <c>EnableExtraAnimations</c>.
/// </summary>
internal sealed class TileTiltEffect : ShaderEffect
{
    private static readonly PixelShader? Shader = Load();

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(TileTiltEffect), 0);

    /// <summary>X, Y the tilt, Z the sheen, W the scale.</summary>
    public static readonly DependencyProperty TiltProperty =
        DependencyProperty.Register(nameof(Tilt), typeof(Point4D), typeof(TileTiltEffect), new UIPropertyMetadata(new Point4D(0, 0, 0, 1), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty SheenProperty =
        DependencyProperty.Register(nameof(Sheen), typeof(Point), typeof(TileTiltEffect), new UIPropertyMetadata(new Point(0.5, 0.5), PixelShaderConstantCallback(1)));

    public TileTiltEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TiltProperty);
        UpdateShaderValue(SheenProperty);
    }

    /// <summary>Whether it can be drawn at all: the shader is in this build, and WPF is not in software mode (where an effect is drawn on the processor, every frame).</summary>
    public static bool Works => Shader is not null && RenderCapability.Tier >> 16 >= 2;

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point4D Tilt
    {
        get => (Point4D)GetValue(TiltProperty);
        set => SetValue(TiltProperty, value);
    }

    public Point Sheen
    {
        get => (Point)GetValue(SheenProperty);
        set => SetValue(SheenProperty, value);
    }

    private static PixelShader? Load()
    {
        using var stream = typeof(TileTiltEffect).Assembly.GetManifestResourceStream("HandPegApp.Shaders.TileTilt.ps");
        if (stream is null)
            return null;

        var shader = new PixelShader();
        shader.SetStreamSource(stream);
        shader.Freeze();
        return shader;
    }
}
