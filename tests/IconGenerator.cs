using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TouchPadAdvanced;

internal static class IconGenerator
{
    private static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2f;
        var arc = new RectangleF(rect.X, rect.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Bitmap RenderBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = size / 256.0f;

        // 1. Base Squircle
        var baseRect = new RectangleF(16 * s, 16 * s, 224 * s, 224 * s);
        using var basePath = CreateRoundedRectangle(baseRect, 52 * s);
        using var baseBrush = new LinearGradientBrush(
            new PointF(16 * s, 16 * s), new PointF(240 * s, 240 * s),
            Color.FromArgb(255, 79, 82, 178), Color.FromArgb(255, 45, 46, 104));
        g.FillPath(baseBrush, basePath);

        using var borderPen = new Pen(Color.FromArgb(55, 255, 255, 255), Math.Max(1.0f, 1.5f * s));
        g.DrawPath(borderPen, basePath);

        // 2. Inner Touchpad Surface
        var innerRect = new RectangleF(44 * s, 44 * s, 168 * s, 168 * s);
        using var innerPath = CreateRoundedRectangle(innerRect, 36 * s);
        using var innerBrush = new SolidBrush(Color.FromArgb(255, 56, 57, 123));
        g.FillPath(innerBrush, innerPath);

        using var innerBorderPen = new Pen(Color.FromArgb(35, 255, 255, 255), Math.Max(1.0f, 1.5f * s));
        g.DrawPath(innerBorderPen, innerPath);

        // 3. Bottom Horizontal Gesture Arrow
        float hStroke = Math.Max(2.0f, 8.0f * s);
        using var hBrush = new LinearGradientBrush(
            new PointF(76 * s, 188 * s), new PointF(168 * s, 188 * s),
            Color.FromArgb(255, 128, 234, 255), Color.FromArgb(255, 79, 149, 255));
        using var hPen = new Pen(hBrush, hStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(hPen, 76 * s, 188 * s, 166 * s, 188 * s);

        using var arrowPenH = new Pen(hBrush, hStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var arrowPathH = new GraphicsPath();
        arrowPathH.AddLine(154 * s, 176 * s, 168 * s, 188 * s);
        arrowPathH.AddLine(168 * s, 188 * s, 154 * s, 200 * s);
        g.DrawPath(arrowPenH, arrowPathH);

        // 4. Right Vertical Gesture Arrow
        float vStroke = Math.Max(2.0f, 8.0f * s);
        using var vBrush = new LinearGradientBrush(
            new PointF(188 * s, 168 * s), new PointF(188 * s, 76 * s),
            Color.FromArgb(255, 79, 149, 255), Color.FromArgb(255, 128, 234, 255));
        using var vPen = new Pen(vBrush, vStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(vPen, 188 * s, 164 * s, 188 * s, 78 * s);

        using var arrowPenV = new Pen(vBrush, vStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var arrowPathV = new GraphicsPath();
        arrowPathV.AddLine(176 * s, 90 * s, 188 * s, 76 * s);
        arrowPathV.AddLine(188 * s, 76 * s, 200 * s, 90 * s);
        g.DrawPath(arrowPenV, arrowPathV);

        // 5. Top-Left Inward diagonal gesture curve (Amber / Gold)
        var goldColor = Color.FromArgb(255, 255, 212, 121);
        float arcStroke = Math.Max(2.0f, 7.0f * s);
        using var arcPen = new Pen(goldColor, arcStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawBezier(arcPen,
            new PointF(64 * s, 64 * s),
            new PointF(98 * s, 74 * s),
            new PointF(106 * s, 98 * s),
            new PointF(108 * s, 108 * s));

        // Inward touch contact dots
        using var goldBrush = new SolidBrush(goldColor);
        float dot1R = Math.Max(2.0f, 6.0f * s);
        g.FillEllipse(goldBrush, 64 * s - dot1R, 64 * s - dot1R, dot1R * 2, dot1R * 2);

        if (size >= 32)
        {
            using var haloBrush = new SolidBrush(Color.FromArgb(70, 255, 212, 121));
            float haloR = 14.0f * s;
            g.FillEllipse(haloBrush, 108 * s - haloR, 108 * s - haloR, haloR * 2, haloR * 2);
        }

        float dot2R = Math.Max(2.5f, 8.0f * s);
        g.FillEllipse(goldBrush, 108 * s - dot2R, 108 * s - dot2R, dot2R * 2, dot2R * 2);

        return bmp;
    }

    public static void SaveIcon(string outputPath, int[]? sizes = null)
    {
        sizes ??= [256, 128, 64, 48, 32, 24, 16];
        var pngStreams = new List<byte[]>();

        foreach (int size in sizes)
        {
            using var bmp = RenderBitmap(size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngStreams.Add(ms.ToArray());
        }

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // ICONDIR
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type 1 = icon
        writer.Write((ushort)sizes.Length); // count

        int offset = 6 + (16 * sizes.Length);

        // ICONDIRENTRY
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            byte[] data = pngStreams[i];

            writer.Write((byte)(size >= 256 ? 0 : size)); // width
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0); // color count
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // color planes
            writer.Write((ushort)32); // bit count
            writer.Write((uint)data.Length); // bytes in res
            writer.Write((uint)offset); // image offset

            offset += data.Length;
        }

        // Image data (PNG blocks)
        foreach (byte[] data in pngStreams)
        {
            writer.Write(data);
        }
    }
}
