using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LunchBox;

/// <summary>
/// 年度カレンダーのウインドウ。フォーカスを奪わず最前面に重ねる。タスクバーと Alt+Tab には出さない。
/// クリック（左右どちらでも）で閉じる。
///
/// 描画は WinForms のウインドウに GDI で直接行う（11/17）。以前は WPF で描いていたが、WPF は画面いっぱいの
/// ウインドウを一度出すと、描画用のメモリ（約 17MB）をウインドウを破棄しても返さなかった。
/// ここでは描くときだけ画面 1 枚ぶんの作業用ビットマップを作り、描き終えたらすぐ捨てる（常駐中は持たない）。
/// 36 か月ぶんを部品に分けず、まとめて描く。横 6 か月 × 縦 6 段。1 年度＝上期・下期の 2 段。
/// </summary>
public sealed class CalendarWindow : Form
{
    private CalendarData? _data;
    private string _signature = "";

    /// <summary>名称を出す日（祝日・会社休日）の位置</summary>
    private readonly List<(RectangleF Cell, string Tip)> _tips = new();
    private bool _tipsDirty = true;
    private string? _tipText;
    private RectangleF _tipCell;
    private Rectangle _tipRect;

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);
    private static readonly Color Ink = C(0x22, 0x22, 0x22), Gray = C(0x6B, 0x72, 0x80), Red = C(0xD3, 0x2F, 0x2F), Blue = C(0x15, 0x65, 0xC0),
        Provisional = C(0xE8, 0x93, 0x93), Green = C(0x1B, 0x7A, 0x2E), CompanyBack = C(0xD9, 0xF2, 0xD9),
        Band = C(0xF3, 0xF5, 0xF8), YearBack = C(0xE3, 0xE8, 0xEF), YearInk = C(0x1F, 0x2D, 0x3D),
        MonthBack = C(0xFF, 0xF4, 0xCC), MonthEdge = C(0xE0, 0xA8, 0x00), Grid = C(0xDF, 0xE3, 0xE8), YearEdge = C(0x7B, 0x86, 0x96),
        TipBack = C(0x33, 0x33, 0x33);
    private static readonly string[] WeekNames = { "日", "月", "火", "水", "木", "金", "土" };
    private static readonly string[] DayText = Enumerable.Range(0, 32).Select(i => i.ToString()).ToArray();
    private static readonly string FaceName = PickFace();

    public CalendarWindow()
    {
        Text = "LunchBox 年度カレンダー";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        // 背景の塗りつぶしはせず、OnPaint だけで描く（ちらつき防止。作業用ビットマップに描いてから一度に写す）
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>表示中か（WPF 版と同じ名前で残している）</summary>
    public bool IsVisible => Visible;

    /// <summary>名称を出す日（祝日・会社休日）の数（確認用）</summary>
    internal int TipCount { get { EnsureTips(); return _tips.Count; } }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW | N.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == N.WM_MOUSEACTIVATE)
        {
            // クリックしてもアクティブにしない（入力中のアプリからフォーカスを奪わない）
            m.Result = new IntPtr(N.MA_NOACTIVATE);
            return;
        }
        if (m.Msg == N.WM_DPICHANGED)
        {
            // 拡大率の違うモニタへ移っても、大きさは ShowOn で指定したとおりにする（WinForms に直させない）
            _tipsDirty = true;
            Invalidate();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>指定の領域（物理ピクセル。モニタの作業領域）いっぱいに、フォーカスを奪わずに出す</summary>
    /// <param name="signature">表示内容が変わったかの判定用（日付・設定・祝日データ）。同じならデータを作り直さない</param>
    public void ShowOn(Rectangle area, string signature, Func<CalendarData> makeData)
    {
        if (signature != _signature || _data is null)
        {
            _data = makeData();
            _signature = signature;
            _tipsDirty = true;
            ClearTip();
            Invalidate();
        }
        IntPtr h = Handle;
        N.SetWindowPos(h, N.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, N.SWP_NOACTIVATE);
        Show();
        // 拡大率の違うモニタへ移ると大きさが変わることがあるので、出した後にもう一度合わせる
        N.SetWindowPos(h, N.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, N.SWP_NOACTIVATE | N.SWP_SHOWWINDOW);
        Update(); // この場で描き終える（表示にかかった時間をログに出すため）
    }

    /// <summary>表示するモニタを選ぶ：0=一番右 1=一番左 2=メイン 3=カーソルのあるモニタ。作業領域を返す</summary>
    public static Rectangle PickArea(int mode,
        IReadOnlyList<(Rectangle Bounds, Rectangle Work, bool Primary)> screens, Point cursor)
    {
        if (screens.Count == 0) return new Rectangle(0, 0, 1280, 720);
        var primary = screens.FirstOrDefault(s => s.Primary);
        if (primary.Work.Width == 0) primary = screens[0];
        var pick = mode switch
        {
            1 => screens.OrderBy(s => s.Bounds.Left).First(),
            2 => primary,
            3 => screens.Any(s => s.Bounds.Contains(cursor)) ? screens.First(s => s.Bounds.Contains(cursor)) : primary,
            _ => screens.OrderByDescending(s => s.Bounds.Left).First(),
        };
        return pick.Work;
    }

    // ---------- 操作 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Hide();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateTip(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearTip();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        ClearTip();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _tipsDirty = true;
        ClearTip();
    }

    private void ClearTip()
    {
        if (_tipText is null) return;
        _tipText = null;
        if (IsHandleCreated) Invalidate(Rectangle.Inflate(_tipRect, 2, 2));
    }

    /// <summary>カーソルの下が祝日・会社休日なら、その名称を近くに出す</summary>
    private void UpdateTip(Point p)
    {
        EnsureTips();
        foreach (var (cell, tip) in _tips)
        {
            if (!cell.Contains(p)) continue;
            if (_tipText == tip && _tipCell == cell) return;
            ClearTip();
            float s = DpiScale();
            var size = MeasureTip(tip, s);
            int tw = size.Width + (int)MathF.Round(16 * s), th = size.Height + (int)MathF.Round(8 * s);
            float x = Math.Min(cell.Right + 4 * s, Math.Max(0, ClientSize.Width - tw - 4 * s));
            float y = Math.Max(0, cell.Top - th - 2 * s);
            _tipText = tip;
            _tipCell = cell;
            _tipRect = new Rectangle((int)MathF.Round(x), (int)MathF.Round(y), tw, th);
            Invalidate(Rectangle.Inflate(_tipRect, 2, 2));
            return;
        }
        ClearTip();
    }

    private static Size MeasureTip(string text, float s)
    {
        IntPtr dc = N.CreateCompatibleDC(IntPtr.Zero);
        IntPtr font = MakeFont(14 * s, false, s);
        try
        {
            IntPtr old = N.SelectObject(dc, font);
            N.GetTextExtentPoint32W(dc, text, text.Length, out var size);
            N.SelectObject(dc, old);
            return new Size(size.cx, size.cy);
        }
        finally
        {
            N.DeleteObject(font);
            N.DeleteDC(dc);
        }
    }

    private void EnsureTips()
    {
        if (!_tipsDirty) return;
        _tips.Clear();
        _tipsDirty = false;
        int w = ClientSize.Width, h = ClientSize.Height;
        if (_data is null || w < 200 || h < 200) return;
        var ctx = new Ctx { Pass = Pass.Tips, M = new Metrics(w, h, DpiScale()), Clip = new RectangleF(0, 0, w, h) };
        Render(ctx, _data);
    }

    // ---------- 描画 ----------

    private float DpiScale()
    {
        try { return IsHandleCreated ? Math.Max(0.5f, N.GetDpiForWindow(Handle) / 96f) : 1f; }
        catch { return 1f; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, w, h));
        if (clip.IsEmpty) return;
        float s = DpiScale();

        IntPtr target = e.Graphics.GetHdc();
        try
        {
            // 作業用ビットマップに描いてから、変わった範囲だけ画面へ写す。描き終えたらすぐ捨てる（常駐中は持たない）
            IntPtr mem = N.CreateCompatibleDC(target);
            IntPtr bmp = N.CreateCompatibleBitmap(target, w, h);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero)
            {
                if (bmp != IntPtr.Zero) N.DeleteObject(bmp);
                if (mem != IntPtr.Zero) N.DeleteDC(mem);
                return;
            }
            IntPtr oldBmp = N.SelectObject(mem, bmp);
            try
            {
                DrawAll(mem, w, h, clip, s);
                N.BitBlt(target, clip.X, clip.Y, clip.Width, clip.Height, mem, clip.X, clip.Y, N.SRCCOPY);
            }
            finally
            {
                N.SelectObject(mem, oldBmp);
                N.DeleteObject(bmp);
                N.DeleteDC(mem);
            }
        }
        finally
        {
            e.Graphics.ReleaseHdc(target);
        }
    }

    private enum Pass { Tips, Shapes, Text }

    /// <summary>画面の大きさから決まる寸法（ピクセル）。S＝拡大率（100% で 1）</summary>
    private readonly struct Metrics
    {
        public readonly float W, H, S, GroupH, HeadH, MonthW, MonthH, RowH, ColW, FontPx;
        public Metrics(float w, float h, float s)
        {
            W = w; H = h; S = s;
            GroupH = h / 3;
            HeadH = Math.Clamp(GroupH * 0.07f, 16 * s, 30 * s);
            MonthW = w / 6;
            MonthH = (GroupH - HeadH) / 2;
            RowH = (MonthH - 8 * s) / 8;
            ColW = (MonthW - 16 * s) / 7;
            FontPx = Math.Min(RowH * 0.82f, ColW * 0.5f);
        }
    }

    /// <summary>1 回の描画（または位置の計算）で使う道具</summary>
    private sealed class Ctx
    {
        public Pass Pass;
        public Metrics M;
        public RectangleF Clip;
        public Graphics? G;
        public IntPtr Hdc;
        public IntPtr FTitle, FDay, FDayBold, FWeek, FYear;
        public Pen? GridPen, YearPen, MonthPen;
        public SolidBrush? Brush;
    }

    private void DrawAll(IntPtr mem, int w, int h, Rectangle clip, float s)
    {
        var data = _data;
        var ctx = new Ctx { M = new Metrics(w, h, s), Clip = clip };
        bool drawable = data is not null && w >= 200 && h >= 200;

        // 1) 図形（背景・帯・罫線・今月と今日の印）
        using (var g = Graphics.FromHdc(mem))
        using (var brush = new SolidBrush(Color.White))
        using (var gridPen = new Pen(Grid, Math.Max(1f, s)))
        using (var yearPen = new Pen(YearEdge, Math.Max(1f, 2 * s)))
        using (var monthPen = new Pen(MonthEdge, Math.Max(1f, 1.5f * s)))
        {
            g.FillRectangle(brush, clip);
            if (drawable)
            {
                ctx.Pass = Pass.Shapes;
                ctx.G = g; ctx.Brush = brush; ctx.GridPen = gridPen; ctx.YearPen = yearPen; ctx.MonthPen = monthPen;
                Render(ctx, data!);
                ctx.G = null; ctx.Brush = null;
            }
        }

        // 2) 文字（GDI で直接描く。約 1,400 個あるので、1 個ずつ管理メモリを使わない方法にしている）
        N.SetBkMode(mem, N.TRANSPARENT);
        var m = ctx.M;
        ctx.FTitle = MakeFont(m.FontPx, true, s);
        ctx.FDay = MakeFont(m.FontPx, false, s);
        ctx.FDayBold = MakeFont(m.FontPx, true, s);
        ctx.FWeek = MakeFont(m.FontPx * 0.74f, false, s);
        ctx.FYear = MakeFont(m.HeadH * 0.66f, true, s);
        IntPtr fTip = IntPtr.Zero;
        try
        {
            if (drawable)
            {
                IntPtr old = N.SelectObject(mem, ctx.FDay);
                ctx.Pass = Pass.Text;
                ctx.Hdc = mem;
                Render(ctx, data!);
                N.SelectObject(mem, old);
            }

            // 3) 祝日・会社休日の名称（文字の上に重ねる）
            if (_tipText is not null && _tipRect.IntersectsWith(clip))
            {
                using (var g = Graphics.FromHdc(mem))
                using (var back = new SolidBrush(TipBack))
                using (var path = Rounded(_tipRect, 4 * s))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(back, path);
                }
                fTip = MakeFont(14 * s, false, s);
                IntPtr old = N.SelectObject(mem, fTip);
                DrawString(mem, fTip, _tipText, _tipRect, Color.White, N.DT_CENTER);
                N.SelectObject(mem, old);
            }
        }
        finally
        {
            N.DeleteObject(ctx.FTitle); N.DeleteObject(ctx.FDay); N.DeleteObject(ctx.FDayBold); N.DeleteObject(ctx.FWeek); N.DeleteObject(ctx.FYear);
            if (fTip != IntPtr.Zero) N.DeleteObject(fTip);
        }
    }

    /// <summary>3 年度 × 12 か月をたどる。Pass によって、図形を描く／文字を描く／名称を出す日の位置を集める</summary>
    private void Render(Ctx c, CalendarData data)
    {
        var m = c.M;
        int fy = data.Start.Year;
        for (int g = 0; g < 3; g++)
        {
            float y0 = g * m.GroupH;
            if (c.Pass == Pass.Shapes)
            {
                var gr = c.G!;
                gr.SmoothingMode = SmoothingMode.None;
                if (g % 2 == 1) Fill(c, Band, new RectangleF(0, y0, m.W, m.GroupH));
                Fill(c, YearBack, new RectangleF(0, y0, m.W, m.HeadH));
                if (g > 0) gr.DrawLine(c.YearPen!, 0, y0, m.W, y0);
                for (int col = 1; col < 6; col++) gr.DrawLine(c.GridPen!, col * m.MonthW, y0 + m.HeadH, col * m.MonthW, y0 + m.GroupH);
                gr.DrawLine(c.GridPen!, 0, y0 + m.HeadH + m.MonthH, m.W, y0 + m.HeadH + m.MonthH);
            }
            else if (c.Pass == Pass.Text)
            {
                DrawString(c.Hdc, c.FYear, FiscalCalendar.FiscalYearName(fy + g), new RectangleF(10 * m.S, y0, m.W - 10 * m.S, m.HeadH), YearInk, N.DT_LEFT);
            }
            for (int r = 0; r < 2; r++)
                for (int col = 0; col < 6; col++)
                {
                    var rect = new RectangleF(col * m.MonthW, y0 + m.HeadH + r * m.MonthH, m.MonthW, m.MonthH);
                    // 描き直しが要る範囲にかからない月は飛ばす（名称を出す日の位置は全部の月で集める）
                    if (c.Pass != Pass.Tips && !rect.IntersectsWith(c.Clip)) continue;
                    DrawMonth(c, data, rect, data.Start.AddMonths(g * 12 + r * 6 + col));
                }
        }
    }

    private void DrawMonth(Ctx c, CalendarData data, RectangleF rect, DateOnly first)
    {
        var m = c.M;
        float s = m.S;
        bool current = first.Year == data.Today.Year && first.Month == data.Today.Month;
        if (c.Pass == Pass.Shapes && current)
        {
            var gr = c.G!;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(rect.X + 2 * s, rect.Y + 2 * s, rect.Width - 4 * s, rect.Height - 4 * s), 4 * s);
            c.Brush!.Color = MonthBack;
            gr.FillPath(c.Brush, path);
            gr.DrawPath(c.MonthPen!, path);
            gr.SmoothingMode = SmoothingMode.None;
        }

        var inner = new RectangleF(rect.X + 8 * s, rect.Y + 4 * s, rect.Width - 16 * s, rect.Height - 8 * s);
        float rowH = inner.Height / 8, colW = inner.Width / 7;

        if (c.Pass == Pass.Text)
        {
            DrawString(c.Hdc, c.FTitle, $"{first.Year}年{first.Month}月", new RectangleF(inner.X, inner.Y, inner.Width, rowH), Ink, N.DT_CENTER);
            for (int i = 0; i < 7; i++)
            {
                int wd = (i + (data.WeekStartsMonday ? 1 : 0)) % 7;
                DrawString(c.Hdc, c.FWeek, WeekNames[wd], new RectangleF(inner.X + i * colW, inner.Y + rowH, colW, rowH),
                    wd == 0 ? Red : wd == 6 ? Blue : Gray, N.DT_CENTER);
            }
        }

        int shift = data.WeekStartsMonday ? 6 : 0;
        int days = DateTime.DaysInMonth(first.Year, first.Month);
        int row = 0;
        for (int day = 1; day <= days; day++)
        {
            var d = new DateOnly(first.Year, first.Month, day);
            int dow = (int)d.DayOfWeek;
            int col = (dow + shift) % 7;
            if (day > 1 && col == 0) row++;
            var cell = new RectangleF(inner.X + col * colW, inner.Y + (2 + row) * rowH, colW, rowH);
            var info = data.Resolve(d);
            bool today = d == data.Today;

            switch (c.Pass)
            {
                case Pass.Shapes:
                    if (info.Company) Fill(c, CompanyBack, new RectangleF(cell.X + s, cell.Y + s, cell.Width - 2 * s, cell.Height - 2 * s));
                    if (today)
                    {
                        var gr = c.G!;
                        gr.SmoothingMode = SmoothingMode.AntiAlias;
                        using var path = Rounded(new RectangleF(cell.X + 1.5f * s, cell.Y + 0.5f * s, cell.Width - 3 * s, cell.Height - s), 4 * s);
                        c.Brush!.Color = Blue;
                        gr.FillPath(c.Brush, path);
                        gr.SmoothingMode = SmoothingMode.None;
                    }
                    break;

                case Pass.Text:
                    Color ink = today ? Color.White
                        : info.Kind == DayKind.Holiday ? Red
                        : info.Kind == DayKind.ProvisionalHoliday ? Provisional
                        : dow == 0 ? Red
                        : dow == 6 ? Blue
                        : info.Company ? Green
                        : Ink;
                    DrawString(c.Hdc, today ? c.FDayBold : c.FDay, DayText[day], cell, ink, N.DT_CENTER);
                    break;

                case Pass.Tips:
                    string tip = info.Tip;
                    if (tip.Length > 0) _tips.Add((cell, $"{d.Month}/{d.Day}（{WeekNames[dow]}）{tip}"));
                    break;
            }
        }
    }

    private static void Fill(Ctx c, Color color, RectangleF r)
    {
        c.Brush!.Color = color;
        c.G!.FillRectangle(c.Brush, r);
    }

    /// <summary>四角の中に 1 行の文字を描く（上下は中央。左右は align で指定）</summary>
    private static void DrawString(IntPtr hdc, IntPtr font, string text, RectangleF r, Color color, uint align)
    {
        N.SelectObject(hdc, font);
        N.SetTextColor(hdc, color.R | (color.G << 8) | (color.B << 16));
        var rc = new N.RECT
        {
            Left = (int)MathF.Round(r.Left), Top = (int)MathF.Round(r.Top),
            Right = (int)MathF.Round(r.Right), Bottom = (int)MathF.Round(r.Bottom),
        };
        N.DrawTextW(hdc, text, text.Length, ref rc, align | N.DT_VCENTER | N.DT_SINGLELINE | N.DT_NOCLIP | N.DT_NOPREFIX);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>文字の高さ（ピクセル）を指定してフォントを作る。使い終わったら DeleteObject で捨てる</summary>
    private static IntPtr MakeFont(float px, bool bold, float s)
    {
        int height = -(int)MathF.Round(Math.Max(6 * s, px));
        return N.CreateFontW(height, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, N.DEFAULT_CHARSET, 0, 0, N.CLEARTYPE_QUALITY, 0, FaceName);
    }

    /// <summary>使う書体：Yu Gothic UI → Meiryo UI → Segoe UI の順で、入っているもの</summary>
    private static string PickFace()
    {
        foreach (string name in new[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI" })
        {
            try { using var f = new FontFamily(name); return name; }
            catch { }
        }
        return "Segoe UI";
    }

    private static class N
    {
        public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
        public const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WM_DPICHANGED = 0x02E0;
        public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public const int SRCCOPY = 0x00CC0020, TRANSPARENT = 1;
        public const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOCLIP = 0x100, DT_NOPREFIX = 0x800;
        public const uint DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5;
        public static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int cx, int cy, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out SIZE size);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
            uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    }
}
