using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length != 1)
{
    Console.Error.WriteLine("Pemakaian: Agent.IconGenerator <output.ico>");
    return 2;
}

var outputPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
    ?? throw new InvalidOperationException("Output icon tidak memiliki direktori."));

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = sizes.Select(size => EncodePng(Render(size))).ToArray();

await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
{
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Length);

    var offset = 6 + (16 * frames.Length);
    for (var index = 0; index < frames.Length; index++)
    {
        writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
        writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)frames[index].Length);
        writer.Write((uint)offset);
        offset += frames[index].Length;
    }

    foreach (var frame in frames)
    {
        writer.Write(frame);
    }
}

await output.FlushAsync();
var previewPath = Path.ChangeExtension(outputPath, ".png");
await File.WriteAllBytesAsync(previewPath, EncodePng(Render(512)));
Console.WriteLine($"Icon multi-resolution dibuat: {outputPath}");
Console.WriteLine($"Preview PNG dibuat: {previewPath}");
return 0;

static byte[] EncodePng(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static RenderTargetBitmap Render(int size) => size <= 24
    ? RenderCompact(size)
    : RenderFull(size);

static RenderTargetBitmap RenderCompact(int size)
{
    var navy = Brush("#0F172A");
    var cyan = Brush("#0EA5E9");
    var paperBlue = Brush("#E0F2FE");
    var white = Brush("#FFFFFF");

    var visual = new DrawingVisual();
    using (var drawing = visual.RenderOpen())
    {
        // Frame kecil sengaja memakai bentuk lebih sederhana agar simbol printer tetap
        // terbaca tajam pada title bar 100–150% DPI, bukan miniatur logo 512px yang kabur.
        var unit = size / 16d;
        drawing.DrawRoundedRectangle(navy, null, new Rect(unit, unit, 14 * unit, 14 * unit), 3 * unit, 3 * unit);
        drawing.DrawRoundedRectangle(paperBlue, null, new Rect(5 * unit, 3 * unit, 6 * unit, 5 * unit), unit, unit);
        drawing.DrawRoundedRectangle(white, null, new Rect(3.5 * unit, 6 * unit, 9 * unit, 6 * unit), 1.5 * unit, 1.5 * unit);
        drawing.DrawRoundedRectangle(navy, null, new Rect(5 * unit, 8 * unit, 6 * unit, 1.25 * unit), 0.6 * unit, 0.6 * unit);
        drawing.DrawRoundedRectangle(paperBlue, null, new Rect(5 * unit, 10 * unit, 6 * unit, 3.5 * unit), 0.7 * unit, 0.7 * unit);
        drawing.DrawEllipse(cyan, null, new Point(10.75 * unit, 7.5 * unit), unit, unit);
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

static RenderTargetBitmap RenderFull(int size)
{
    var navy = Brush("#0F172A");
    var navyHighlight = Brush("#111F38");
    var cyan = Brush("#0EA5E9");
    var paperBlue = Brush("#E0F2FE");
    var white = Brush("#FFFFFF");
    var paper = Brush("#F8FAFC");
    var slate = Brush("#334155");
    var muted = Brush("#94A3B8");

    var visual = new DrawingVisual();
    using (var drawing = visual.RenderOpen())
    {
        drawing.PushTransform(new ScaleTransform(size / 1024d, size / 1024d));
        drawing.DrawRoundedRectangle(navy, null, new Rect(24, 24, 976, 976), 224, 224);

        var highlight = new StreamGeometry();
        using (var context = highlight.Open())
        {
            context.BeginFigure(new Point(24, 690), true, true);
            context.LineTo(new Point(474, 240), true, false);
            context.LineTo(new Point(1000, 240), true, false);
            context.LineTo(new Point(1000, 426), true, false);
            context.LineTo(new Point(426, 1000), true, false);
            context.LineTo(new Point(248, 1000), true, false);
            context.BezierTo(new Point(124, 1000), new Point(24, 900), new Point(24, 776), true, false);
        }

        highlight.Freeze();
        drawing.DrawGeometry(navyHighlight, null, highlight);
        drawing.DrawRoundedRectangle(paperBlue, null, new Rect(310, 224, 404, 286), 36, 36);
        drawing.DrawRoundedRectangle(white, null, new Rect(238, 388, 548, 350), 74, 74);
        drawing.DrawRoundedRectangle(navy, null, new Rect(306, 492, 412, 52), 26, 26);
        drawing.DrawEllipse(cyan, null, new Point(700, 474), 22, 22);
        drawing.DrawRoundedRectangle(paper, null, new Rect(324, 614, 376, 214), 28, 28);
        drawing.DrawRoundedRectangle(slate, null, new Rect(382, 678, 260, 24), 12, 12);
        drawing.DrawRoundedRectangle(muted, null, new Rect(382, 734, 188, 24), 12, 12);
        drawing.DrawEllipse(navy, null, new Point(772, 252), 94, 94);
        drawing.DrawEllipse(cyan, null, new Point(772, 252), 68, 68);
        drawing.Pop();
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

static SolidColorBrush Brush(string value)
{
    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    brush.Freeze();
    return brush;
}
