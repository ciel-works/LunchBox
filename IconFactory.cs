using System.Drawing;
using System.Drawing.Drawing2D;

namespace LunchBox;

/// <summary>トレイ用アイコン（お弁当箱）を実行時に描画する</summary>
internal static class IconFactory
{
    /// <summary>トレイ用アイコン：フタを開けて起動（弁当箱から上向き矢印。LunchBox × Launcher。11/11 変更）</summary>
    public static Icon CreateTrayIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 16f;
            using (var arrow = new SolidBrush(Color.FromArgb(0x37, 0x8A, 0xDD)))
                g.FillPolygon(arrow, new[]
                {
                    new PointF(8f * k, 1f * k), new PointF(11.5f * k, 5f * k), new PointF(9.3f * k, 5f * k),
                    new PointF(9.3f * k, 8.5f * k), new PointF(6.7f * k, 8.5f * k), new PointF(6.7f * k, 5f * k),
                    new PointF(4.5f * k, 5f * k),
                });
            using (var path = RoundedRect(new RectangleF(0.5f * k, 9.5f * k, 15f * k, 6f * k), 1.5f * k))
            using (var box = new SolidBrush(Color.FromArgb(0xEF, 0x9F, 0x27)))
                g.FillPath(box, path);
            using (var path = RoundedRect(new RectangleF(0.5f * k, 9.5f * k, 15f * k, 1.8f * k), 0.9f * k))
            using (var lid = new SolidBrush(Color.FromArgb(0xBA, 0x75, 0x17)))
                g.FillPath(lid, path);
        }
        IntPtr h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        NativeMethods.DestroyIcon(h);
        return icon;
    }

    /// <summary>ロケット（11/6〜11/10 のトレイアイコン。現在は未使用）</summary>
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size;

        var red = Color.FromArgb(255, 224, 70, 60);
        var body = Color.FromArgb(255, 236, 240, 246);
        var bodyEdge = Color.FromArgb(255, 150, 160, 175);
        var flameOuter = Color.FromArgb(255, 255, 150, 30);
        var flameInner = Color.FromArgb(255, 255, 230, 90);
        var glass = Color.FromArgb(255, 70, 150, 230);

        // 炎
        using (var b = new SolidBrush(flameOuter))
            g.FillEllipse(b, s * 0.36f, s * 0.70f, s * 0.28f, s * 0.30f);
        using (var b = new SolidBrush(flameInner))
            g.FillEllipse(b, s * 0.43f, s * 0.74f, s * 0.14f, s * 0.20f);

        // フィン（左右）
        using (var b = new SolidBrush(red))
        {
            g.FillPolygon(b, new[] { new PointF(s * 0.34f, s * 0.56f), new PointF(s * 0.14f, s * 0.84f), new PointF(s * 0.36f, s * 0.78f) });
            g.FillPolygon(b, new[] { new PointF(s * 0.66f, s * 0.56f), new PointF(s * 0.86f, s * 0.84f), new PointF(s * 0.64f, s * 0.78f) });
        }

        // 胴体（上は丸く、下はすぼめる）
        using (var path = new GraphicsPath())
        {
            path.AddArc(s * 0.32f, s * 0.08f, s * 0.36f, s * 0.36f, 180, 180);
            path.AddLine(s * 0.68f, s * 0.26f, s * 0.64f, s * 0.80f);
            path.AddLine(s * 0.64f, s * 0.80f, s * 0.36f, s * 0.80f);
            path.AddLine(s * 0.36f, s * 0.80f, s * 0.32f, s * 0.26f);
            path.CloseFigure();
            using (var b = new SolidBrush(body)) g.FillPath(b, path);
            using (var pen = new Pen(bodyEdge, Math.Max(1f, s * 0.035f))) g.DrawPath(pen, path);
        }

        // 先端（赤）
        using (var path = new GraphicsPath())
        {
            path.AddArc(s * 0.32f, s * 0.08f, s * 0.36f, s * 0.36f, 180, 180);
            path.AddLine(s * 0.68f, s * 0.26f, s * 0.32f, s * 0.26f);
            path.CloseFigure();
            using (var b = new SolidBrush(red)) g.FillPath(b, path);
        }

        // 窓
        using (var b = new SolidBrush(glass))
            g.FillEllipse(b, s * 0.41f, s * 0.36f, s * 0.18f, s * 0.18f);
        using (var pen = new Pen(bodyEdge, Math.Max(1f, s * 0.035f)))
            g.DrawEllipse(pen, s * 0.41f, s * 0.36f, s * 0.18f, s * 0.18f);

        return bmp;
    }

    /// <summary>メニュー用：クリップボードのアイコン（32px）</summary>
    public static Bitmap DrawClipboard(int size = 32)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size;
        using (var path = RoundedRect(new RectangleF(s * 0.18f, s * 0.14f, s * 0.64f, s * 0.78f), s * 0.08f))
        using (var brush = new SolidBrush(Color.FromArgb(255, 196, 150, 90)))
            g.FillPath(brush, path);
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, s * 0.27f, s * 0.26f, s * 0.46f, s * 0.58f);
        using (var brush = new SolidBrush(Color.FromArgb(255, 90, 90, 90)))
            g.FillRectangle(brush, s * 0.36f, s * 0.06f, s * 0.28f, s * 0.14f);
        using (var pen = new Pen(Color.FromArgb(255, 150, 150, 150), s * 0.05f))
        {
            g.DrawLine(pen, s * 0.33f, s * 0.42f, s * 0.67f, s * 0.42f);
            g.DrawLine(pen, s * 0.33f, s * 0.54f, s * 0.67f, s * 0.54f);
            g.DrawLine(pen, s * 0.33f, s * 0.66f, s * 0.56f, s * 0.66f);
        }
        return bmp;
    }

    /// <summary>メニュー用：文字（テキスト）のアイコン（32px）</summary>
    public static Bitmap DrawTextGlyph(int size = 32)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size;
        using (var path = RoundedRect(new RectangleF(s * 0.12f, s * 0.12f, s * 0.76f, s * 0.76f), s * 0.12f))
        using (var brush = new SolidBrush(Color.FromArgb(255, 70, 110, 170)))
            g.FillPath(brush, path);
        using var f = new Font("Segoe UI", s * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("T", f, Brushes.White, new RectangleF(0, s * 0.02f, s, s), sf);
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath RoundedRectTop(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        p.CloseFigure();
        return p;
    }
}
