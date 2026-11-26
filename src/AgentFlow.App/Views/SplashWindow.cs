using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AgentFlow.App.Services;

namespace AgentFlow.App.Views;

/// <summary>
/// The animated splash shown while AgentFlow starts (the permission server, the database, the studios).
/// The pixel art is drawn at a whole multiple of its size in screen pixels with nearest-neighbour scaling,
/// so it stays crisp at any Windows display scale.
/// </summary>
public sealed class SplashWindow : Window
{
    private static readonly Uri Gif = new("pack://application:,,,/AgentFlow;component/Assets/splash.gif");

    private readonly GifAnimation? _gif;
    private readonly DateTime _shownAt = DateTime.UtcNow;

    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; // something to see in the taskbar while starting
        Title = "AgentFlow";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/AgentFlow;component/Assets/AgentFlow.ico"));
        WindowStartupLocation = WindowStartupLocation.Manual;

        var image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        try
        {
            _gif = GifAnimation.Load(Gif);
            _gif.Play(image);
        }
        catch (Exception ex)
        {
            AppLog.Write("Splash: could not load the animation", ex); // start without it
        }

        Content = new Border
        {
            Margin = new Thickness(24), // room for the shadow
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0B, 0x14)),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.45 },
            Child = new Border
            {
                CornerRadius = new CornerRadius(14),
                ClipToBounds = true,
                Child = image,
            },
        };
        // Round the image's corners to match the frame.
        image.Loaded += (_, _) => image.Clip = new RectangleGeometry(new Rect(image.RenderSize), 14, 14);
        image.SizeChanged += (_, _) => image.Clip = new RectangleGeometry(new Rect(image.RenderSize), 14, 14);

        SourceInitialized += (_, _) => SizeAndCenter();
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>The time one loop of the animation takes (the loading bar's sweep), or zero without it.</summary>
    public TimeSpan LoopDuration => _gif?.LoopDuration ?? TimeSpan.Zero;

    /// <summary>Waits until the splash has been up for at least <paramref name="minimum"/>.</summary>
    public Task WaitShownAsync(TimeSpan minimum)
    {
        var left = minimum - (DateTime.UtcNow - _shownAt);
        return left > TimeSpan.Zero ? Task.Delay(left) : Task.CompletedTask;
    }

    /// <summary>Fades out and closes.</summary>
    public void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void SizeAndCenter()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var gifWidth = _gif?.PixelWidth ?? 960;
        var gifHeight = _gif?.PixelHeight ?? 540;
        var area = SystemParameters.WorkArea; // in device-independent units
        var areaPixels = area.Width * dpi.DpiScaleX;
        // The largest whole multiple of the art that takes at most half the screen width (at least 1x),
        // shrunk only if even 1x does not fit.
        var scale = Math.Max(1, Math.Floor(areaPixels * 0.5 / gifWidth));
        var widthPx = gifWidth * scale;
        if (widthPx > areaPixels * 0.9) scale = areaPixels * 0.9 / gifWidth;

        var margin = 48; // the shadow margin on both sides, in DIPs
        Width = gifWidth * scale / dpi.DpiScaleX + margin;
        Height = gifHeight * scale / dpi.DpiScaleY + margin;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }
}
