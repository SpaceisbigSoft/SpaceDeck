using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceDeck.App.Services.Media;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Controls;

/// <summary>
/// A picture or GIF from a file (<see cref="MediaPath"/>), filling its box (cropped) or fitting inside it. GIFs play,
/// looping at their own frame delays, while the element is loaded: on screen, and in the cooler screen's offscreen
/// window, which renders whatever frame is showing. Files are decoded off the UI thread and shared (<see cref="MediaCache"/>);
/// a missing or broken file shows nothing.
/// </summary>
public sealed class AnimatedImage : FrameworkElement
{
    public static readonly DependencyProperty MediaPathProperty = DependencyProperty.Register(
        nameof(MediaPath), typeof(string), typeof(AnimatedImage), new FrameworkPropertyMetadata(null, (d, _) => ((AnimatedImage)d).Reload()));

    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(ImageFit), typeof(AnimatedImage), new FrameworkPropertyMetadata(ImageFit.Fill, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One clock for every animated picture, so copies of a GIF (a tile and the cooler's copy of it) play in step.</summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly TimeSpan ShortestWait = TimeSpan.FromMilliseconds(10);

    private readonly DispatcherTimer _timer;
    private AnimationClip? _clip;
    private GifComposer? _composer;
    private WriteableBitmap? _bitmap;
    private int _generation;

    public AnimatedImage()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Advance();
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _timer.Stop();
    }

    public string? MediaPath
    {
        get => (string?)GetValue(MediaPathProperty);
        set => SetValue(MediaPathProperty, value);
    }

    public ImageFit Fit
    {
        get => (ImageFit)GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_bitmap is not { } bitmap || _clip is not { } clip || RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        var placed = ScreenGeometry.ImageRect(clip.Width, clip.Height, RenderSize.Width, RenderSize.Height, Fit);
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        drawingContext.DrawImage(bitmap, new Rect(placed.X, placed.Y, placed.Width, placed.Height));
        drawingContext.Pop();
    }

    private void Reload()
    {
        var generation = ++_generation;
        _timer.Stop();
        _clip = null;
        _composer = null;
        _bitmap = null;
        InvalidateVisual();
        if (IsLoaded && MediaPath is { Length: > 0 } path)
        {
            _ = LoadAsync(path, generation);
        }
    }

    private async Task LoadAsync(string path, int generation)
    {
        AnimationClip clip;
        try
        {
            clip = await MediaCache.LoadAsync(path);
        }
        catch (Exception)
        {
            return; // missing, unreadable or not a picture: nothing to show (the tile's settings say which file)
        }

        if (generation != _generation || !IsLoaded)
        {
            return; // another file was chosen meanwhile, or the element went away
        }

        _clip = clip;
        _composer = new GifComposer(clip);
        _bitmap = new WriteableBitmap(clip.Width, clip.Height, 96, 96, PixelFormats.Bgra32, null);
        Show(clip.IsAnimated ? AnimationTimeline.At(clip, Clock.Elapsed).Index : 0);
        InvalidateVisual();
        if (clip.IsAnimated)
        {
            Advance();
        }
    }

    /// <summary>Shows the frame due now and waits for the next one.</summary>
    private void Advance()
    {
        _timer.Stop();
        if (_clip is not { IsAnimated: true } clip || _composer is null || !IsLoaded)
        {
            return;
        }

        var now = Clock.Elapsed;
        var (index, _, next) = AnimationTimeline.At(clip, now);
        if (index != _composer.Index)
        {
            Show(index);
        }

        var wait = (next ?? now + AnimationTimeline.ShortDelayReplacement) - now;
        _timer.Interval = wait < ShortestWait ? ShortestWait : wait;
        _timer.Start();
    }

    private void Show(int index)
    {
        if (_composer is null || _bitmap is null)
        {
            return;
        }

        _composer.MoveTo(index);
        var canvas = _composer.Canvas;
        _bitmap.WritePixels(new Int32Rect(0, 0, canvas.Width, canvas.Height), canvas.Pixels, canvas.Stride, 0);
    }
}
