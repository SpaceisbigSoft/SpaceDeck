using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceDeck.App.ViewModels;
using SpaceDeck.App.Views;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Services;

/// <summary>
/// Draws a dashboard offscreen for a cooler screen: the real <see cref="DashboardCanvas"/> (the same tile templates, the
/// active skin), cut to the tiles' area, scaled to fit the screen and rendered with <see cref="RenderTargetBitmap"/>.
/// </summary>
/// <remarks>
/// <para>UI thread only (the dashboard's tiles and sensors live there). The canvas sits in a hidden
/// <see cref="HwndSource"/> (never shown, no taskbar button, never activated), so it is a real presentation source:
/// tiles get Loaded (sparklines and charts attach to their data) and resources resolve as in a window.</para>
/// <para>The canvas is laid out at the full 12-column width, so tiles keep their places (no reflow), and the result is
/// clipped to the columns and rows the tiles use: a 6-column dashboard fills a square screen.</para>
/// </remarks>
internal sealed class CoolerScreenRenderer : IDisposable
{
    /// <summary>The canvas's own margin around the tiles (DashboardCanvas ScaleHost: 6 above, 16 right, 12 below).</summary>
    private const double CanvasTop = 6;
    private const double CanvasRight = 16;
    private const double CanvasBottom = 12;

    /// <summary>Space kept free at the screen's edges, as a share of its size.</summary>
    private const double EdgeMargin = 0.04;

    private readonly Border _outer;
    private readonly Border _root;
    private readonly Border _clip;
    private readonly DashboardCanvas _canvas;
    private HwndSource? _source;

    public CoolerScreenRenderer()
    {
        _canvas = new DashboardCanvas
        {
            IsEditable = false,
            RowMenus = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -CanvasTop, 0, 0),
        };
        _clip = new Border
        {
            ClipToBounds = true,
            Child = _canvas,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _root = new Border
        {
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = _clip },
            SnapsToDevicePixels = true,
        };

        // As a dashboard window: the skin's background, text color and font.
        _root.SetResourceReference(Border.BackgroundProperty, "Skin.Background");
        _root.SetResourceReference(TextElement.ForegroundProperty, "Skin.Text");
        _root.SetResourceReference(TextElement.FontFamilyProperty, "Skin.Font.Body");

        // An opaque layer under it: a JPEG has no transparency, and see-through skins would otherwise turn black.
        _outer = new Border { Child = _root, Background = Brushes.Black };
    }

    /// <summary>
    /// Draws <paramref name="dashboard"/> at exactly <paramref name="width"/> × <paramref name="height"/> pixels, turned
    /// clockwise by <paramref name="rotation"/> (0, 90, 180, 270). The result is frozen, so it can be encoded on another thread.
    /// </summary>
    public BitmapSource Render(DashboardDocumentViewModel dashboard, int width, int height, int rotation)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        rotation = CoolerScreenSettings.NormalizeRotation(rotation);

        // Drawn upright at the turned size, then rotated into the screen's own width × height.
        var turned = rotation is 90 or 270;
        var drawWidth = turned ? height : width;
        var drawHeight = turned ? width : height;

        if (!ReferenceEquals(_canvas.DataContext, dashboard))
        {
            _canvas.DataContext = dashboard;
        }

        var (contentWidth, contentHeight) = ContentSize(dashboard.Document.Tiles);
        _clip.Width = contentWidth;
        _clip.Height = contentHeight;
        _canvas.Width = GridLayout.FullWidthPx + CanvasRight;
        _canvas.Height = CanvasTop + contentHeight + CanvasBottom + 2; // a pixel or two spare, so no scrollbar shows

        _outer.Width = drawWidth;
        _outer.Height = drawHeight;
        _outer.Background = OpaqueBackground();
        _root.Padding = new Thickness(Math.Round(Math.Min(drawWidth, drawHeight) * EdgeMargin));

        EnsureSource(drawWidth, drawHeight);
        _outer.Measure(new Size(drawWidth, drawHeight));
        _outer.Arrange(new Rect(0, 0, drawWidth, drawHeight));
        _outer.UpdateLayout();

        var bitmap = new RenderTargetBitmap(drawWidth, drawHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_outer);
        bitmap.Freeze();
        if (rotation == 0)
        {
            return bitmap;
        }

        var rotated = new TransformedBitmap(bitmap, new RotateTransform(rotation));
        rotated.Freeze();
        return rotated;
    }

    /// <summary>
    /// A baseline JPEG of <paramref name="frame"/> at <paramref name="quality"/>, lowered in steps (down to
    /// <see cref="CoolerScreenSettings.MinQuality"/>) until it fits in <paramref name="maxBytes"/>; null if it never does.
    /// Any thread (the frame is frozen).
    /// </summary>
    public static byte[]? EncodeJpeg(BitmapSource frame, int quality, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);
        for (var q = Math.Clamp(quality, CoolerScreenSettings.MinQuality, CoolerScreenSettings.MaxQuality); ; q -= 10)
        {
            q = Math.Max(q, CoolerScreenSettings.MinQuality);
            var encoder = new JpegBitmapEncoder { QualityLevel = q };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= maxBytes)
            {
                return stream.ToArray();
            }

            if (q == CoolerScreenSettings.MinQuality)
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            _source.RootVisual = null;
            _source.Dispose();
            _source = null;
        }

        _canvas.DataContext = null;
    }

    /// <summary>The area the tiles cover, in pixels at 100 % (an empty dashboard gets a 6 × 10 cell area for its hint).</summary>
    internal static (double Width, double Height) ContentSize(IReadOnlyCollection<TileSpec> tiles)
    {
        var columns = tiles.Count == 0 ? 6 : Math.Clamp(tiles.Max(t => t.Col + t.ColSpan), 1, GridLayout.Columns);
        var rows = tiles.Count == 0 ? 10 : Math.Clamp(tiles.Max(t => t.Row + t.RowSpan), 1, GridLayout.MaxRow);
        return ((columns * GridLayout.ColumnPitchPx) - GridLayout.GapPx, (rows * GridLayout.RowPitchPx) - GridLayout.GapPx);
    }

    /// <summary>The skin's background without transparency (black when it isn't a plain color).</summary>
    private Brush OpaqueBackground()
    {
        if (_root.TryFindResource("Skin.Background") is SolidColorBrush { Color: var color })
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        return Brushes.Black;
    }

    private void EnsureSource(int width, int height)
    {
        if (_source is not null)
        {
            return;
        }

        const int WsPopup = unchecked((int)0x80000000);
        const int WsExToolWindow = 0x00000080;
        const int WsExNoActivate = 0x08000000;
        var parameters = new HwndSourceParameters("SpaceDeck cooler screen")
        {
            WindowStyle = WsPopup, // no WS_VISIBLE: the window is never shown
            ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
            PositionX = -32000,
            PositionY = -32000,
            Width = width,
            Height = height,
        };
        _source = new HwndSource(parameters) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = _outer };
    }
}
