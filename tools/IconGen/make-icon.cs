#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

// Draws the AgentFlow icon and writes a multi-size .ico (16 to 256 px) plus a 256 px PNG preview.
// The mark: three stages joined by a flowing line, the last one ticked, on a deep indigo-to-teal tile.
//   dotnet run tools/IconGen/make-icon.cs -- <output folder> [<installer folder>]
// With an installer folder it also writes the WiX wizard bitmaps Dialog.bmp (493x312) and Banner.bmp (493x58).

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
Directory.CreateDirectory(outDir);

if (args.Length > 1)
{
    var installerDir = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(installerDir);
    WriteBmp(Path.Combine(installerDir, "Dialog.bmp"), DialogBitmap());
    WriteBmp(Path.Combine(installerDir, "Banner.bmp"), BannerBitmap());
    Console.WriteLine($"Wrote installer bitmaps to {installerDir}");
}

// Welcome and finish pages: a coloured panel on the left with the mark and name; the wizard writes its text on the white right side.
static BitmapSource DialogBitmap()
{
    const int w = 493, h = 312, panel = 164;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var bg = new LinearGradientBrush(Color.FromRgb(0x43, 0x38, 0xCA), Color.FromRgb(0x0E, 0xA5, 0xB7), new Point(0, 0), new Point(1, 1));
        dc.DrawRectangle(bg, null, new Rect(0, 0, panel, h));
        DrawMark(dc, new Rect((panel - 96) / 2.0, 70, 96, 96), onTile: false);
        var name = new FormattedText("AgentFlow", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 22, Brushes.White, 1.0);
        dc.DrawText(name, new Point((panel - name.Width) / 2, 182));
        var tag = new FormattedText("runs Claude Code", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.0);
        dc.DrawText(tag, new Point((panel - tag.Width) / 2, 214));
    }
    return Rasterize(dv, w, h);
}

// Inner pages: white banner with the small icon on the right; the wizard writes the page title on the left.
static BitmapSource BannerBitmap()
{
    const int w = 493, h = 58;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var icon = BitmapFrame.Create(new MemoryStream(Render(48)));
        dc.DrawImage(icon, new Rect(w - 48 - 8, 5, 48, 48));
    }
    return Rasterize(dv, w, h);
}

static BitmapSource Rasterize(DrawingVisual dv, int w, int h)
{
    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    return new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0); // MSI dialogs want plain 24-bit BMPs
}

static void WriteBmp(string path, BitmapSource source)
{
    var enc = new BmpBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(source));
    using var fs = File.Create(path);
    enc.Save(fs);
}

// The white mark on its own (no tile), for the coloured dialog panel.
static void DrawMark(DrawingContext dc, Rect box, bool onTile)
{
    Point P(double x, double y) => new(box.X + x * box.Width, box.Y + y * box.Height);
    var s = box.Width;
    var a = P(0.22, 0.70);
    var b = P(0.50, 0.30);
    var c = P(0.78, 0.70);
    var line = new Pen(Brushes.White, s * 0.075) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    var flow = new StreamGeometry();
    using (var g = flow.Open())
    {
        g.BeginFigure(a, false, false);
        g.BezierTo(P(0.28, 0.40), P(0.38, 0.30), b, true, true);
        g.BezierTo(P(0.62, 0.30), P(0.72, 0.40), c, true, true);
    }
    dc.DrawGeometry(null, line, flow);
    var r = s * 0.12;
    var indigo = new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA));
    dc.DrawEllipse(Brushes.White, null, a, r, r);
    dc.DrawEllipse(Brushes.White, null, b, r, r);
    dc.DrawEllipse(indigo, null, a, r * 0.45, r * 0.45);
    dc.DrawEllipse(indigo, null, b, r * 0.45, r * 0.45);
    var rc = r * 1.35;
    dc.DrawEllipse(Brushes.White, null, c, rc, rc);
    var tick = new StreamGeometry();
    using (var g = tick.Open())
    {
        g.BeginFigure(new Point(c.X - rc * 0.48, c.Y + rc * 0.02), false, false);
        g.LineTo(new Point(c.X - rc * 0.12, c.Y + rc * 0.36), true, true);
        g.LineTo(new Point(c.X + rc * 0.50, c.Y - rc * 0.32), true, true);
    }
    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0x0E, 0x9F, 0xB5)), rc * 0.26) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, tick);
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var pngs = new List<byte[]>();
foreach (var size in sizes)
{
    var png = Render(size);
    pngs.Add(png);
    if (size == 256) File.WriteAllBytes(Path.Combine(outDir, "AgentFlow-256.png"), png);
}
WriteIco(Path.Combine(outDir, "AgentFlow.ico"), sizes, pngs);
Console.WriteLine($"Wrote {Path.Combine(outDir, "AgentFlow.ico")}");

static byte[] Render(int px)
{
    var s = (double)px;
    var small = px <= 24; // fewer details where they would turn to mush
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        // Tile: rounded square with a diagonal gradient and a soft top highlight.
        var inset = small ? 0.0 : s * 0.03;
        var tile = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        var radius = s * 0.22;
        var bg = new LinearGradientBrush(Color.FromRgb(0x43, 0x38, 0xCA), Color.FromRgb(0x0E, 0xA5, 0xB7), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(bg, null, tile, radius, radius);
        if (!small)
        {
            var gloss = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0.5, 0), new Point(0.5, 0.6));
            dc.DrawRoundedRectangle(gloss, null, tile, radius, radius);
        }

        // Three stages: plan (left), build (top middle), review (right), joined by one flowing line.
        Point P(double x, double y) => new(x * s, y * s);
        var a = P(0.25, 0.66);
        var b = P(0.50, 0.36);
        var c = P(0.75, 0.66);
        var white = Brushes.White;
        var lineWidth = Math.Max(1.4, s * (small ? 0.085 : 0.065));
        var line = new Pen(white, lineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        var flow = new StreamGeometry();
        using (var g = flow.Open())
        {
            g.BeginFigure(a, false, false);
            g.BezierTo(P(0.30, 0.42), P(0.40, 0.36), b, true, true);
            g.BezierTo(P(0.60, 0.36), P(0.70, 0.42), c, true, true);
        }
        flow.Freeze();
        dc.DrawGeometry(null, line, flow);

        var r = s * (small ? 0.13 : 0.105);
        var teal = new SolidColorBrush(Color.FromRgb(0x0E, 0x9F, 0xB5));
        var indigo = new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA));

        // Plan and build: white nodes with a coloured core.
        dc.DrawEllipse(white, null, a, r, r);
        dc.DrawEllipse(white, null, b, r, r);
        if (!small)
        {
            dc.DrawEllipse(indigo, null, a, r * 0.45, r * 0.45);
            dc.DrawEllipse(indigo, null, b, r * 0.45, r * 0.45);
        }

        // Review: a larger white node with a tick, the work done.
        var rc = r * 1.35;
        dc.DrawEllipse(white, null, c, rc, rc);
        var tick = new StreamGeometry();
        using (var g = tick.Open())
        {
            g.BeginFigure(new Point(c.X - rc * 0.48, c.Y + rc * 0.02), false, false);
            g.LineTo(new Point(c.X - rc * 0.12, c.Y + rc * 0.36), true, true);
            g.LineTo(new Point(c.X + rc * 0.50, c.Y - rc * 0.32), true, true);
        }
        tick.Freeze();
        dc.DrawGeometry(null, new Pen(teal, Math.Max(1.2, rc * 0.26)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, tick);
    }

    var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// ICO with PNG-compressed images (supported since Windows Vista).
static void WriteIco(string path, int[] sizes, List<byte[]> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0);            // reserved
    w.Write((ushort)1);            // type: icon
    w.Write((ushort)sizes.Length); // count
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // width (0 = 256)
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // height
        w.Write((byte)0);   // palette
        w.Write((byte)0);   // reserved
        w.Write((ushort)1); // colour planes
        w.Write((ushort)32);
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}
