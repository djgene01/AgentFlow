using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AgentFlow.App.Views;

/// <summary>
/// An animated GIF as ready-to-show frames. WPF's Image shows only a GIF's first frame, so the frames are
/// decoded and composited here (offsets, transparency and the disposal methods), then played as a key-frame
/// animation of Image.Source on WPF's own clock.
/// </summary>
public sealed class GifAnimation
{
    public IReadOnlyList<BitmapSource> Frames { get; }
    public IReadOnlyList<TimeSpan> Delays { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public TimeSpan LoopDuration { get; }

    private GifAnimation(List<BitmapSource> frames, List<TimeSpan> delays, int width, int height)
    {
        Frames = frames;
        Delays = delays;
        PixelWidth = width;
        PixelHeight = height;
        LoopDuration = delays.Aggregate(TimeSpan.Zero, (a, b) => a + b);
    }

    public static GifAnimation Load(Uri uri)
    {
        using var stream = Application.GetResourceStream(uri)?.Stream ?? throw new FileNotFoundException("No resource", uri.ToString());
        return Load(stream);
    }

    public static GifAnimation Load(Stream stream)
    {
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var first = decoder.Frames[0];
        var width = Query<ushort>(decoder.Metadata, "/logscrdesc/Width") is var w and > 0 ? w : first.PixelWidth;
        var height = Query<ushort>(decoder.Metadata, "/logscrdesc/Height") is var h and > 0 ? h : first.PixelHeight;

        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);
        BitmapSource? background = null; // what the next frame is drawn over
        var full = new Rect(0, 0, width, height);

        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            var rect = new Rect(Query<ushort>(meta, "/imgdesc/Left"), Query<ushort>(meta, "/imgdesc/Top"), frame.PixelWidth, frame.PixelHeight);
            var disposal = Query<byte>(meta, "/grctlext/Disposal");
            var delayCs = Query<ushort>(meta, "/grctlext/Delay");
            // Browsers treat 0 and 1 hundredths as "as fast as possible" and use 100 ms instead.
            delays.Add(TimeSpan.FromMilliseconds(delayCs <= 1 ? 100 : delayCs * 10));

            var composed = Render(width, height, dc =>
            {
                if (background is not null) dc.DrawImage(background, full);
                dc.DrawImage(frame, rect);
            });
            frames.Add(composed);

            background = disposal switch
            {
                2 => Render(width, height, dc => // restore that area to transparent
                {
                    if (background is null) return;
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(rect)));
                    dc.DrawImage(background, full);
                    dc.Pop();
                }),
                3 => background,  // restore to what was there before this frame
                _ => composed,    // leave it in place
            };
        }
        return new GifAnimation(frames, delays, width, height);
    }

    /// <summary>Plays the frames on an Image, looping forever.</summary>
    public void Play(Image image)
    {
        var anim = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = LoopDuration };
        var at = TimeSpan.Zero;
        for (var i = 0; i < Frames.Count; i++)
        {
            anim.KeyFrames.Add(new DiscreteObjectKeyFrame(Frames[i], KeyTime.FromTimeSpan(at)));
            at += Delays[i];
        }
        anim.Freeze();
        image.Source = Frames[0];
        image.BeginAnimation(Image.SourceProperty, anim);
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static T Query<T>(ImageMetadata? meta, string query) where T : struct
    {
        try
        {
            return meta is BitmapMetadata m && m.GetQuery(query) is T value ? value : default;
        }
        catch (Exception) when (meta is not null)
        {
            return default; // missing block: the GIF default
        }
    }
}
