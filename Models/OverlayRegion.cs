using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>What an element shows.</summary>
public enum ElementKind
{
    /// <summary>A rectangle cut out of the source video (a minimap, an ammo counter).</summary>
    Video,

    /// <summary>A picture from a file (a logo, a frame, a caption).</summary>
    Image,

    /// <summary>The box the auto-captions are drawn in. There is one, and it is always the top layer.</summary>
    Captions,
}

/// <summary>
/// Something the Frame &amp; Layer Engine places on the output frame: a rectangle of the source video, or an
/// image file. Both kinds are positioned, sized and styled in exactly the same way.
///
/// Everything is stored as a fraction, 0 to 1: the source rectangle relative to the source frame, and the
/// position and size relative to the output frame. A layout made for one resolution therefore fits any
/// other: switching the frame from 1080 to 720 wide moves and sizes every element with it.
/// </summary>
public sealed partial class OverlayRegion : ObservableObject
{
    [ObservableProperty] private string _name = "Element";

    public ElementKind Kind { get; init; }

    /// <summary>The picture of an image element.</summary>
    public string ImagePath { get; init; } = "";

    // The picture's own size in pixels, which gives a locked image element its shape.
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    public bool IsImage => Kind == ElementKind.Image;

    public bool IsCaptions => Kind == ElementKind.Captions;

    /// <summary>Corners, feathering and shadow shape a picture; the caption box has only its opacity.</summary>
    public bool HasShapeStyle => !IsCaptions;

    /// <summary>The caption box is part of the captions: it comes and goes with them, and is not removed by hand.</summary>
    public bool IsRemovable => !IsCaptions;

    /// <summary>Only a piece of the video has a place in the source to be marked.</summary>
    public bool IsVideo => Kind == ElementKind.Video;

    // Where the element is in the source, as fractions of the source frame.
    [ObservableProperty] private double _sourceX;
    [ObservableProperty] private double _sourceY;
    [ObservableProperty] private double _sourceWidth = 0.25;
    [ObservableProperty] private double _sourceHeight = 0.25;

    // Where its top-left corner goes, as fractions of the output frame.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionXPercent))]
    private double _positionX = 0.28;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionYPercent))]
    private double _positionY = 0.03;

    /// <summary>The position as percentages of the frame, for the number boxes beside the X and Y sliders.</summary>
    public double PositionXPercent
    {
        get => Math.Round(PositionX * 100, 1);
        set => PositionX = Math.Clamp(value, 0, 100) / 100;
    }

    public double PositionYPercent
    {
        get => Math.Round(PositionY * 100, 1);
        set => PositionY = Math.Clamp(value, 0, 100) / 100;
    }

    /// <summary>Width as a fraction of the output frame's width.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizePercent))]
    private double _sizeWidth = 0.44;

    /// <summary>Height as a fraction of the output frame's height. Only used while the aspect ratio is unlocked.</summary>
    [ObservableProperty] private double _sizeHeight;

    /// <summary>While locked, the element keeps the shape of its source rectangle; unlocked, it can be stretched.</summary>
    [ObservableProperty] private bool _lockAspectRatio = true;

    // Style: rounded corners, soft edges, transparency and a drop shadow, so the element sits on the
    // picture instead of being pasted on it.

    /// <summary>Corner rounding as a percentage of the element's shorter side: 0 is square, 50 a full half-circle.</summary>
    [ObservableProperty] private int _cornerRadius;

    /// <summary>100 is solid; lower lets the picture underneath show through.</summary>
    [ObservableProperty] private int _opacity = 100;

    [ObservableProperty] private bool _feather;
    [ObservableProperty] private int _featherRadius = 12;
    [ObservableProperty] private bool _shadow;
    [ObservableProperty] private double _shadowOpacity = 0.5;
    [ObservableProperty] private int _shadowOffset = 10;

    /// <summary>The width as a whole percentage of the frame width, for typing.</summary>
    public int SizePercent
    {
        get => (int)Math.Round(SizeWidth * 100);
        set => SizeWidth = Math.Clamp(value, 1, 200) / 100.0;
    }

    public bool IsUsable => SizeWidth > 0 && (IsImage ? ImagePath.Length > 0 : SourceWidth > 0 && SourceHeight > 0);

    /// <summary>The source rectangle in pixels of a source of the given size.</summary>
    public (int X, int Y, int Width, int Height) GetSourceRect(int sourceWidth, int sourceHeight) =>
        ((int)Math.Round(SourceX * sourceWidth), (int)Math.Round(SourceY * sourceHeight),
            Math.Max(Even(SourceWidth * sourceWidth), 2), Math.Max(Even(SourceHeight * sourceHeight), 2));

    /// <summary>
    /// Where and how large the element is on an output frame of the given size, in pixels. The source
    /// size is needed for the shape of a locked element; when it is unknown, 16:9 is assumed.
    /// </summary>
    public (int X, int Y, int Width, int Height) GetOutputRect(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight)
    {
        var width = Math.Max(Even(SizeWidth * frameWidth), 2);

        var height = LockAspectRatio || SizeHeight <= 0
            ? GetProportionalHeight(width, sourceWidth, sourceHeight)
            : Math.Max(Even(SizeHeight * frameHeight), 2);

        return ((int)Math.Round(PositionX * frameWidth), (int)Math.Round(PositionY * frameHeight), width, height);
    }

    /// <summary>The height that keeps the element's own shape at a given width: the source rectangle's, or the picture's.</summary>
    private int GetProportionalHeight(int width, int sourceWidth, int sourceHeight)
    {
        if (IsImage)
            return ImageWidth > 0 && ImageHeight > 0 ? Math.Max(Even((double)width * ImageHeight / ImageWidth), 2) : width;

        var (_, _, cutWidth, cutHeight) = GetSourceRect(sourceWidth > 0 ? sourceWidth : 1920, sourceHeight > 0 ? sourceHeight : 1080);
        return Math.Max(Even((double)width * cutHeight / cutWidth), 2);
    }

    /// <summary>Called when the lock is released: the free height starts from the shape the element has now.</summary>
    public void StartFreeHeight(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight)
    {
        if (frameHeight <= 0)
            return;

        var width = Math.Max(Even(SizeWidth * frameWidth), 2);
        SizeHeight = (double)GetProportionalHeight(width, sourceWidth, sourceHeight) / frameHeight;
    }

    private static int Even(double value) => (int)Math.Round(value / 2) * 2;

    public OverlayRegionState ToState() => new()
    {
        Name = Name,
        Kind = Kind,
        ImagePath = ImagePath,
        ImageWidth = ImageWidth,
        ImageHeight = ImageHeight,
        CornerRadius = CornerRadius,
        Opacity = Opacity,
        SourceX = SourceX,
        SourceY = SourceY,
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        PositionX = PositionX,
        PositionY = PositionY,
        SizeWidth = SizeWidth,
        SizeHeight = SizeHeight,
        LockAspectRatio = LockAspectRatio,
        Feather = Feather,
        FeatherRadius = FeatherRadius,
        Shadow = Shadow,
        ShadowOpacity = ShadowOpacity,
        ShadowOffset = ShadowOffset,
    };

    /// <summary>
    /// Rebuilds an element from saved state. Presets saved before positions became fractions hold pixel
    /// values; those are converted using the sizes given (or 1920 x 1080 and 1080 x 1920 when unknown).
    /// </summary>
    public static OverlayRegion FromState(OverlayRegionState state, int sourceWidth, int sourceHeight, int frameWidth, int frameHeight)
    {
        var region = new OverlayRegion
        {
            Name = state.Name,
            Kind = state.Kind,
            ImagePath = state.ImagePath,
            ImageWidth = state.ImageWidth,
            ImageHeight = state.ImageHeight,
            CornerRadius = Math.Clamp(state.CornerRadius, 0, 50),
            Opacity = Math.Clamp(state.Opacity, 0, 100),
            LockAspectRatio = state.LockAspectRatio,
            Feather = state.Feather,
            FeatherRadius = state.FeatherRadius,
            Shadow = state.Shadow,
            ShadowOpacity = state.ShadowOpacity,
            ShadowOffset = state.ShadowOffset,
        };

        if (state.SourceWidth <= 0 && state.Width is > 0 && state.Height is > 0)
        {
            double sw = sourceWidth > 0 ? sourceWidth : 1920, sh = sourceHeight > 0 ? sourceHeight : 1080;
            double fw = frameWidth > 0 ? frameWidth : 1080, fh = frameHeight > 0 ? frameHeight : 1920;
            region.SourceX = (state.X ?? 0) / sw;
            region.SourceY = (state.Y ?? 0) / sh;
            region.SourceWidth = state.Width.Value / sw;
            region.SourceHeight = state.Height.Value / sh;
            region.PositionX = (state.OffsetX ?? 0) / fw;
            region.PositionY = (state.OffsetY ?? 0) / fh;
            region.SizeWidth = (state.OutputWidth ?? 480) / fw;
            region.SizeHeight = (state.FreeHeight ?? 0) / fh;
        }
        else
        {
            region.SourceX = state.SourceX;
            region.SourceY = state.SourceY;
            region.SourceWidth = state.SourceWidth;
            region.SourceHeight = state.SourceHeight;
            region.PositionX = state.PositionX;
            region.PositionY = state.PositionY;
            region.SizeWidth = state.SizeWidth;
            region.SizeHeight = state.SizeHeight;
        }

        return region;
    }
}

/// <summary>An element as it is written to presets and projects.</summary>
public sealed class OverlayRegionState
{
    public string Name { get; set; } = "Element";

    public ElementKind Kind { get; set; }
    public string ImagePath { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public int CornerRadius { get; set; }
    public int Opacity { get; set; } = 100;

    public double SourceX { get; set; }
    public double SourceY { get; set; }
    public double SourceWidth { get; set; }
    public double SourceHeight { get; set; }

    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double SizeWidth { get; set; }
    public double SizeHeight { get; set; }

    public bool LockAspectRatio { get; set; } = true;

    public bool Feather { get; set; }
    public int FeatherRadius { get; set; } = 12;
    public bool Shadow { get; set; }
    public double ShadowOpacity { get; set; } = 0.5;
    public int ShadowOffset { get; set; } = 10;

    // Pixel values from presets saved by earlier versions. Read for conversion, never written.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputWidth { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OffsetX { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OffsetY { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? FreeHeight { get; set; }
}
