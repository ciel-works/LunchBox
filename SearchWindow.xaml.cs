using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace LunchBox;

/// <summary>
/// トレイアイコンの左クリック／ホットキーで出る検索ウインドウ。
/// 全アプリのピン留め＋「最近」を1つの一覧にして、入力した文字で絞り込む。
/// 左端にアプリのアイコン、その隣に項目のアイコンを出す。
/// </summary>
public partial class SearchWindow : Window
{
    public sealed class Row
    {
        /// <summary>アプリの項目のとき。クリップボードの項目は null</summary>
        public AppEntry? App { get; init; }
        public string AppName => App?.Name ?? "クリップボード";
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public DateTime When { get; init; }
        public string WhenText => When == DateTime.MinValue ? "" : When.ToString("MM/dd HH:mm");
        public bool IsPin { get; init; }
        public string KindText => Clip is not null ? (Clip.Pinned ? "クリップ★" : "クリップ") : (IsPin ? "ピン留め" : "最近");
        public ItemKind Kind { get; init; }
        public string Target { get; init; } = "";
        public PinItem? Pin { get; init; }
        public ClipItem? Clip { get; init; }
        public ImageSource? AppIcon { get; init; }
        public Func<ImageSource?>? ItemIconLoader { get; init; }
        private ImageSource? _itemIcon;
        private bool _loaded;
        public ImageSource? ItemIcon
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try { _itemIcon = ItemIconLoader?.Invoke(); }
                    catch { _itemIcon = null; }
                }
                return _itemIcon;
            }
        }
        /// <summary>絞り込み用（小文字）</summary>
        public string SearchText { get; init; } = "";
    }

    private readonly Func<AppSettings> _getSettings;
    private readonly Func<HistoryStore> _getHistory;
    private readonly Func<ClipStore?> _getClips;
    private readonly Func<ClipboardMonitor?> _getClipboard;
    private List<Row> _all = new();
    private readonly Dictionary<string, ImageSource?> _appIcons = new();
    private ImageSource? _clipAppIcon;
    private ImageSource? _textGlyph;
    private bool _suppressDeactivate;

    /// <summary>項目を開いたとき（保存などに使う）</summary>
    public event Action<AppEntry, PinItem>? PinOpened;

    public SearchWindow(Func<AppSettings> getSettings, Func<HistoryStore> getHistory,
                        Func<ClipStore?> getClips, Func<ClipboardMonitor?> getClipboard)
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ReleaseRows(); };
        _getSettings = getSettings;
        _getHistory = getHistory;
        _getClips = getClips;
        _getClipboard = getClipboard;
    }

    /// <summary>カーソルの近くに出して、入力欄にフォーカスを入れる</summary>
    public void ShowNearCursor()
    {
        Reload();
        FilterBox.Text = "";
        Placeholder.Visibility = Visibility.Visible;

        // 位置：カーソルの上側に収まるように（作業領域内に収める）
        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? (System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920) / SystemParameters.PrimaryScreenWidth;
        if (scale <= 0) scale = 1;
        double w = Width * scale, h = Height * scale;
        double left = cursor.X - w / 2, top = cursor.Y - h - 12;
        if (left + w > area.Right) left = area.Right - w;
        if (left < area.Left) left = area.Left;
        if (top < area.Top) top = Math.Min(cursor.Y + 12, area.Bottom - h);
        if (top < area.Top) top = area.Top;
        Left = left / scale;
        Top = top / scale;

        _suppressDeactivate = true;
        Show();
        Activate();
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        _suppressDeactivate = false;
    }

    private void Reload()
    {
        var settings = _getSettings();
        var history = _getHistory();
        var rows = new List<Row>();
        foreach (var app in settings.Apps)
        {
            if (!_appIcons.TryGetValue(app.Id, out var appIcon))
            {
                appIcon = IconCache.ToBitmapSource(IconCache.ForApp(app));
                _appIcons[app.Id] = appIcon;
            }
            foreach (var pin in app.Pins)
            {
                var p = pin;
                rows.Add(new Row
                {
                    App = app, AppIcon = appIcon, IsPin = true, Pin = pin, Kind = pin.Kind, Target = pin.Target,
                    Title = pin.DisplayLabel, Sub = pin.Target, When = pin.LastChecked ?? DateTime.MinValue,
                    ItemIconLoader = () => IconCache.ToBitmapSource(IconCache.ForPin(p)),
                    SearchText = (app.Name + " " + pin.DisplayLabel + " " + pin.Target).ToLowerInvariant(),
                });
            }
            foreach (var r in history.Get(app.Id, int.MaxValue, app.Pins.Select(p => p.Target)))
            {
                var rec = r;
                string title = string.IsNullOrWhiteSpace(r.Title) ? PinItem.DefaultLabel(r.Kind, r.Target) : r.Title;
                rows.Add(new Row
                {
                    App = app, AppIcon = appIcon, IsPin = false, Kind = r.Kind, Target = r.Target,
                    Title = title, Sub = r.Target, When = r.When,
                    ItemIconLoader = () => IconCache.ToBitmapSource(IconCache.ForTarget(rec.Kind, rec.Target)),
                    SearchText = (app.Name + " " + title + " " + r.Target).ToLowerInvariant(),
                });
            }
        }

        // クリップボードの履歴（文字・画像）
        var clips = settings.ClipboardEnabled ? _getClips() : null;
        if (clips is not null)
        {
            _clipAppIcon ??= IconCache.ToBitmapSource(IconFactory.DrawClipboard());
            _textGlyph ??= IconCache.ToBitmapSource(IconFactory.DrawTextGlyph());
            foreach (var c in clips.Snapshot())
            {
                var clip = c;
                string sub = c.Kind == ClipKind.Image ? $"画像 {c.Width}×{c.Height}" : $"{(c.Text ?? "").Length} 文字";
                string body = c.Kind == ClipKind.Text ? (c.Text ?? "") : "";
                if (body.Length > 2000) body = body.Substring(0, 2000);
                rows.Add(new Row
                {
                    App = null, AppIcon = _clipAppIcon, IsPin = c.Pinned, Clip = c, Kind = ItemKind.File, Target = "",
                    Title = c.Label, Sub = sub, When = c.When,
                    ItemIconLoader = () => clip.Kind == ClipKind.Image ? IconCache.ToBitmapSource(clips.Thumbnail(clip)) : _textGlyph,
                    SearchText = ("クリップボード " + c.Label + " " + body).ToLowerInvariant(),
                });
            }
        }

        // ピン留めを先頭、その後は新しい順
        _all = rows.OrderByDescending(r => r.IsPin).ThenByDescending(r => r.When).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim().ToLowerInvariant();
        List<Row> rows;
        if (f.Length == 0) rows = _all;
        else
        {
            var words = f.Split(' ', '　').Where(w => w.Length > 0).ToArray();
            rows = _all.Where(r => words.All(w => r.SearchText.Contains(w))).ToList();
        }
        List.ItemsSource = rows;
        CountText.Text = f.Length == 0 ? $"{rows.Count} 件" : $"{rows.Count} / {_all.Count} 件";
        if (rows.Count > 0) { List.SelectedIndex = 0; List.ScrollIntoView(rows[0]); }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 入力欄にカーソルを置いたまま ↑↓ で一覧を選ぶ
        int n = List.Items.Count;
        if (n == 0) return;
        switch (e.Key)
        {
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(+10); e.Handled = true; break;
            case Key.PageUp: Move(-10); e.Handled = true; break;
            case Key.Home when Keyboard.Modifiers == ModifierKeys.Control: Select(0); e.Handled = true; break;
            case Key.End when Keyboard.Modifiers == ModifierKeys.Control: Select(n - 1); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        int n = List.Items.Count;
        int i = Math.Clamp(List.SelectedIndex + delta, 0, n - 1);
        Select(i);
    }

    private void Select(int i)
    {
        if (i < 0 || i >= List.Items.Count) return;
        List.SelectedIndex = i;
        List.ScrollIntoView(List.Items[i]);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
    }

    /// <summary>クリック 1 回で開く（11/13 変更。それまではダブルクリック）。行の上で離したときだけ（スクロールバー・見出しは対象外）</summary>
    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not System.Windows.Controls.ListViewItem)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader) return;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        if (d is not System.Windows.Controls.ListViewItem item) return;
        List.SelectedItem = item.DataContext;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (List.SelectedItem is not Row r) return;
        try
        {
            if (r.Clip is not null)
            {
                var mon = _getClipboard();
                if (mon is null || !mon.CopyToClipboard(r.Clip))
                {
                    MessageBox.Show("クリップボードへ戻せませんでした。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else if (r.Pin is not null && r.App is not null)
            {
                Launcher.OpenPin(r.Pin);
                PinOpened?.Invoke(r.App, r.Pin);
            }
            else
            {
                Launcher.OpenTarget(r.Kind, r.Target);
            }
            Hide();
        }
        catch (Exception ex)
        {
            MessageBox.Show("開けませんでした。\n" + ex.Message, AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // 別のウインドウへ移ったら隠す（閉じずに再利用）
        if (!_suppressDeactivate) Hide();
    }

    /// <summary>隠れたら一覧のデータを手放す（数千行ぶんを常駐中ずっと抱えない）。次に開くとき作り直す</summary>
    private void ReleaseRows()
    {
        List.ItemsSource = null;
        _all = new List<Row>();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // × で閉じても破棄せず隠すだけ
        e.Cancel = true;
        Hide();
    }
}
