using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace RemoteMouse.App.Ui;

/// <summary>
/// Tray image matching the Android launcher: #242424 tile and the white RM mark.
/// </summary>
static class TrayIcon
{
    const string MarkPath =
        "M130.42,0L184.7,111.87L238.46,0H277.88V17.41H261.75V174.59H277.88V192H224.12V174.59H240.76V42.75L192.38,142.34H176.25L128.12,41.98V170.7L130.56,174.59H144.9V192H91V174.59H107.13V173.14L64.26,104.19L37.12,120.83V174.59H53.76V192H0V174.59H16.13V17.41H0V0H130.42Z" +
        "M107.13,77.42C104.93,79.06 102.41,80.74 99.58,82.43C94.46,85.5 87.98,89.43 80.13,94.21L107.13,137.24V77.42Z" +
        "M37.12,100.35L101.38,60.67V17.41H37.12V100.35Z";

    static readonly RectangleF MarkBounds = new(0, 0, 277.88f, 192f);

    static MemoryStream? _iconBytes;

    public static Icon Create()
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var images = new List<(int Size, byte[] Png)>(sizes.Length);
        foreach (var size in sizes)
        {
            using var bitmap = Draw(size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            images.Add((size, stream.ToArray()));
        }
        _iconBytes = new MemoryStream(Pack(images));
        return new Icon(_iconBytes);
    }

    public static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var inset = Math.Max(0.5f, size * 0.02f);
        using (var tile = RoundedRect(inset, inset, size - inset * 2, size - inset * 2, size * 0.22f))
        using (var background = new SolidBrush(Color.FromArgb(255, 0x24, 0x24, 0x24)))
            graphics.FillPath(background, tile);

        using var mark = ParseMark();
        var pad = size * 0.18f;
        var box = size - pad * 2;
        var scale = Math.Min(box / MarkBounds.Width, box / MarkBounds.Height);
        var left = (size - MarkBounds.Width * scale) / 2f;
        var top = (size - MarkBounds.Height * scale) / 2f;
        graphics.TranslateTransform(left, top);
        graphics.ScaleTransform(scale, scale);
        graphics.FillPath(Brushes.White, mark);
        return bitmap;
    }

    static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(width, height));
        var arc = new RectangleF(x, y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = x + width - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = y + height - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = x;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    static GraphicsPath ParseMark()
    {
        var path = new GraphicsPath(FillMode.Alternate);
        var index = 0;
        var command = 'M';
        PointF current = default;
        PointF start = default;
        var figureOpen = false;

        while (index < MarkPath.Length)
        {
            Skip(ref index);
            if (index >= MarkPath.Length) break;
            if (char.IsLetter(MarkPath[index]))
                command = MarkPath[index++];

            switch (command)
            {
                case 'M':
                    current = Point(ref index);
                    start = current;
                    path.StartFigure();
                    figureOpen = true;
                    command = 'L';
                    break;
                case 'L':
                    current = Line(path, current, Point(ref index));
                    break;
                case 'H':
                    current = Line(path, current, new PointF(Number(ref index), current.Y));
                    break;
                case 'V':
                    current = Line(path, current, new PointF(current.X, Number(ref index)));
                    break;
                case 'C':
                    var c1 = Point(ref index);
                    var c2 = Point(ref index);
                    var end = Point(ref index);
                    path.AddBezier(current, c1, c2, end);
                    current = end;
                    break;
                case 'Z':
                    if (figureOpen)
                        path.CloseFigure();
                    current = start;
                    figureOpen = false;
                    break;
            }
        }

        return path;
    }

    static PointF Line(GraphicsPath path, PointF from, PointF to)
    {
        path.AddLine(from, to);
        return to;
    }

    static PointF Point(ref int index)
    {
        var x = Number(ref index);
        var y = Number(ref index);
        return new PointF(x, y);
    }

    static float Number(ref int index)
    {
        Skip(ref index);
        var start = index;
        if (index < MarkPath.Length && (MarkPath[index] == '-' || MarkPath[index] == '+'))
            index++;
        var dotted = false;
        while (index < MarkPath.Length)
        {
            var ch = MarkPath[index];
            if (char.IsDigit(ch))
            {
                index++;
                continue;
            }
            if (ch == '.' && !dotted)
            {
                dotted = true;
                index++;
                continue;
            }
            break;
        }
        return float.Parse(MarkPath.AsSpan(start, index - start), CultureInfo.InvariantCulture);
    }

    static void Skip(ref int index)
    {
        while (index < MarkPath.Length && (MarkPath[index] is ',' or ' ' or '\n' or '\r' or '\t'))
            index++;
    }

    static byte[] Pack(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var image in images)
        {
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(image.Png.Length);
            writer.Write(offset);
            offset += image.Png.Length;
        }
        foreach (var image in images)
            writer.Write(image.Png);
        writer.Flush();
        return stream.ToArray();
    }
}
