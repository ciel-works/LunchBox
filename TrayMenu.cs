using System.Drawing;
using System.Windows.Forms;

namespace LunchBox;

/// <summary>
/// トレイメニュー（アプリ一覧 → サブメニューにピン留め＋最近）。
/// 項目の右クリックは、メニューを閉じずにその項目の下へ操作メニュー（子メニュー）を開く。
/// メニューは開くたびに作り直す。
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    private sealed record ItemRef(AppEntry App, PinItem? Pin, RecentItem? Recent);

    private readonly RightClickContextMenuStrip _menu = new();
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<HistoryStore> _getHistory;
    private readonly List<Bitmap> _tempImages = new();
    private Bitmap? _clipIcon;
    private Bitmap? _textGlyph;

    /// <summary>クリップボード履歴（App が設定する。null なら項目を出さない）</summary>
    public ClipStore? Clips { get; set; }
    public ClipboardMonitor? Clipboard { get; set; }
    private sealed record ClipRef(ClipItem Item);
    private sealed class ClipboardGroup { }
    private static readonly ClipboardGroup ClipboardGroupTag = new();

    // ---- ウインドウ切替（旧 FrontSwitcher） ----
    /// <summary>（有効か, ジェスチャ有効か, ジェスチャ一時停止中か）を返す。App が設定する</summary>
    public Func<(bool Enabled, bool GestureOn, bool Paused)>? SwitcherState { get; set; }
    public event Action? SwitchNowRequested;
    public event Action? RestoreHiddenRequested;
    public event Action<bool>? GesturePauseChanged;
    public event Action? OpenSwitcherSettingsRequested;

    // ---- ウインドウ配置の記憶 ----
    public WindowLayoutService? Layouts { get; set; }
    public event Action? LayoutSaveRequested;
    public event Action<LayoutSnapshot>? LayoutRecallRequested;
    public event Action<LayoutSnapshot>? LayoutOverwriteRequested;
    public event Action<LayoutSnapshot>? LayoutRenameRequested;
    public event Action<LayoutSnapshot>? LayoutDeleteRequested;
    public event Action? OpenLayoutSettingsRequested;
    public event Action? CalendarRequested;
    public event Action<bool>? NotifyPlaceToggled;

    public event Action? ClipsChanged;
    public event Action? OpenClipboardSettingsRequested;
    /// <summary>子メニューの項目をクリックしたあともメニュー全体を開いたままにする（1回分）</summary>
    private bool _keepOpenOnce;

    public event Action? OpenSettingsRequested;
    public event Action<AppEntry>? ManagePinsRequested;
    public event Action<AppEntry, PinItem>? EditPinRequested;
    /// <summary>設定（ピン留め）や履歴を変更したので保存してほしい</summary>
    public event Action? SettingsChanged;
    public event Action? HistoryChanged;
    public event Action? ExitRequested;
    public event Action<string>? Notify;

    public ContextMenuStrip Strip => _menu;

    public TrayMenu(Func<AppSettings> getSettings, Func<HistoryStore> getHistory)
    {
        _getSettings = getSettings;
        _getHistory = getHistory;
        _menu.ShowImageMargin = true;
        _menu.ShowCheckMargin = false;
        _menu.ShowItemToolTips = true;
        _menu.Opening += (_, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Rebuild();
            Logger.Log($"メニュー構築 {sw.ElapsedMilliseconds}ms");
        };
        _menu.Closing += KeepOpenFilter;
        // トップ階層の右クリック：サブメニューがあるものは開く、直接起動のものは何もしない（誤起動防止）
        _menu.ItemRightClicked += mi =>
        {
            if (mi.HasDropDownItems) mi.ShowDropDown();
        };
        // アプリ（トップ階層）はドラッグ＆ドロップで並べ替えできる
        _menu.CanDrag = mi => mi.Tag is AppEntry;
        _menu.ItemDropped += ReorderAppsByDrop;
        _menu.Closed += (_, _) => DisposeTempImages();
    }

    /// <summary>起動直後に一度だけ呼び、アイコン等のキャッシュを温める（表示はしない）。
    /// 初回の右クリックで組み立てに時間がかかると、前面化の権利が失効してメニューがすぐ閉じるため。</summary>
    public void Warm()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _menu.CreateControl();
            Rebuild();
            Logger.Log($"メニューの事前構築 {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Logger.Log("メニューの事前構築失敗: " + ex.Message);
        }
    }

    private int ScaledIconSize()
    {
        int dpi = 96;
        try { dpi = _menu.DeviceDpi; } catch { }
        return Math.Max(16, 16 * dpi / 96);
    }

    // ---------- メニュー構築 ----------

    private void Rebuild()
    {
        DisposeTempImages();
        DisposeItems(_menu.Items);
        int iconSize = ScaledIconSize();
        _menu.ImageScalingSize = new Size(iconSize, iconSize);

        var settings = _getSettings();
        if (settings.Apps.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("（アプリが登録されていません。設定から追加してください）") { Enabled = false });
        }

        foreach (var app in settings.Apps)
        {
            // ツールチップは付けない（サブメニューに重なって邪魔になるため）
            var appItem = new ToolStripMenuItem(Escape(app.Name))
            {
                Image = IconCache.ForApp(app),
                Tag = app,
            };

            // 直接起動：グループ（サブメニュー）を作らず、クリックでそのまま起動
            // （設定で指定、または ピン留めも「最近」の履歴も無い）
            bool nothingToShow = app.Pins.Count == 0 &&
                                 _getHistory().Count(app.Id) == 0;
            if (app.IsDirectLaunch || nothingToShow)
            {
                var direct = app;
                appItem.ToolTipText = app.ExePath;
                appItem.Click += (_, _) => LaunchApp(direct);
                _menu.Items.Add(appItem);
                continue;
            }

            var dd = new RightClickDropDownMenu();
            appItem.DropDown = dd;
            dd.ItemRightClicked += mi =>
            {
                if (mi.Tag is ItemRef r) ShowItemActions(mi, appItem, r);
            };
            // ピン留め項目はドラッグ＆ドロップで並べ替えできる
            dd.CanDrag = mi => mi.Tag is ItemRef r && r.Pin is not null;
            dd.ItemDropped += (src, dst, after) => ReorderPinsByDrop(appItem, src, dst, after);
            dd.ImageScalingSize = new Size(iconSize, iconSize);
            dd.ShowImageMargin = true;
            dd.ShowCheckMargin = false;
            dd.ShowItemToolTips = true;
            dd.Closing += KeepOpenFilter;
            BuildAppSubMenu(appItem, app, settings);
            // アプリ名のクリックはサブメニューを開くだけ（起動はサブメニューの「このアプリを起動」から）
            _menu.Items.Add(appItem);
        }

        if (settings.ClipboardEnabled && Clips is not null)
        {
            _menu.Items.Add(new ToolStripSeparator());
            _clipIcon ??= IconFactory.DrawClipboard();
            var clipItem = new ToolStripMenuItem("クリップボード") { Image = _clipIcon, Tag = ClipboardGroupTag };
            var dd = new RightClickDropDownMenu();
            clipItem.DropDown = dd;
            dd.ItemRightClicked += mi =>
            {
                if (mi.Tag is ClipRef c) ShowClipActions(mi, clipItem, c);
            };
            dd.ImageScalingSize = new Size(iconSize, iconSize);
            dd.ShowImageMargin = true;
            dd.ShowCheckMargin = false;
            dd.ShowItemToolTips = true;
            dd.Closing += KeepOpenFilter;
            BuildClipboardSubMenu(clipItem, settings);
            _menu.Items.Add(clipItem);
        }

        // ウインドウ切替（旧 FrontSwitcher）
        {
            var st = SwitcherState?.Invoke() ?? (false, false, false);
            var sw = new ToolStripMenuItem("ウインドウ切替");
            sw.DropDownItems.Add(new ToolStripMenuItem("今すぐ切替（テスト）", null, (_, _) => SwitchNowRequested?.Invoke()) { Enabled = st.Item1 });
            sw.DropDownItems.Add(new ToolStripMenuItem("隠したウインドウを戻す", null, (_, _) => RestoreHiddenRequested?.Invoke()) { Enabled = st.Item1 });
            var pause = new ToolStripMenuItem("マウスジェスチャ一時停止") { Checked = st.Item3, Enabled = st.Item1 && st.Item2 };
            pause.Click += (_, _) => GesturePauseChanged?.Invoke(!st.Item3);
            sw.DropDownItems.Add(pause);
            sw.DropDownItems.Add(new ToolStripSeparator());
            sw.DropDownItems.Add("切替の設定...", null, (_, _) => OpenSwitcherSettingsRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(sw);
        }

        // ウインドウ配置の記憶
        if (Layouts is not null)
        {
            var cfg = Layouts.Current;
            var lay = new ToolStripMenuItem("ウインドウ配置");
            lay.DropDownItems.Add(new ToolStripMenuItem("いまの画面：" + cfg.Description) { Enabled = false });
            lay.DropDownItems.Add("いまの配置をセーブ...", null, (_, _) => LayoutSaveRequested?.Invoke());
            lay.DropDownItems.Add(new ToolStripSeparator());
            if (cfg.Saves.Count == 0)
                lay.DropDownItems.Add(new ToolStripMenuItem("（この画面構成のセーブはありません）") { Enabled = false });
            foreach (var s in cfg.Saves)
            {
                var snap = s;
                lay.DropDownItems.Add(new ToolStripMenuItem(Escape(snap.Name) + "　を呼び出す", null, (_, _) => LayoutRecallRequested?.Invoke(snap))
                { ToolTipText = $"{snap.When:MM/dd HH:mm} セーブ・{snap.Windows.Count} ウインドウ" });
            }
            lay.DropDownItems.Add(new ToolStripSeparator());
            var prev = cfg.Previous;
            if (prev.Count == 0)
            {
                lay.DropDownItems.Add(new ToolStripMenuItem("直前の配置に戻す（記録なし）") { Enabled = false });
            }
            else
            {
                var p0 = prev[0];
                lay.DropDownItems.Add(new ToolStripMenuItem($"直前の配置に戻す（{p0.When:MM/dd HH:mm}）", null, (_, _) => LayoutRecallRequested?.Invoke(p0)));
                if (prev.Count > 1)
                {
                    var older = new ToolStripMenuItem("さらに前の配置");
                    foreach (var p in prev.Skip(1))
                    {
                        var snap = p;
                        older.DropDownItems.Add($"{snap.When:MM/dd HH:mm} の配置に戻す", null, (_, _) => LayoutRecallRequested?.Invoke(snap));
                    }
                    lay.DropDownItems.Add(older);
                }
            }
            if (cfg.Saves.Count > 0)
            {
                lay.DropDownItems.Add(new ToolStripSeparator());
                var manage = new ToolStripMenuItem("セーブの管理");
                foreach (var s in cfg.Saves)
                {
                    var snap = s;
                    var one = new ToolStripMenuItem(Escape(snap.Name));
                    one.DropDownItems.Add("いまの配置で上書き", null, (_, _) => LayoutOverwriteRequested?.Invoke(snap));
                    one.DropDownItems.Add("名前を変更...", null, (_, _) => LayoutRenameRequested?.Invoke(snap));
                    one.DropDownItems.Add("削除", null, (_, _) => LayoutDeleteRequested?.Invoke(snap));
                    manage.DropDownItems.Add(one);
                }
                lay.DropDownItems.Add(manage);
            }
            lay.DropDownItems.Add(new ToolStripSeparator());
            lay.DropDownItems.Add("配置の設定...", null, (_, _) => OpenLayoutSettingsRequested?.Invoke());
            _menu.Items.Add(lay);
        }

        if (settings.CalendarEnabled)
            _menu.Items.Add("年度カレンダー", null, (_, _) => CalendarRequested?.Invoke());
        {
            bool on = settings.NotifyPlaceEnabled;
            var np = new ToolStripMenuItem("通知を別のモニタに出す") { Checked = on };
            np.Click += (_, _) => NotifyPlaceToggled?.Invoke(!on);
            _menu.Items.Add(np);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("設定...", null, (_, _) => OpenSettingsRequested?.Invoke());
        _menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());
    }

    // ---------- クリップボード ----------

    private void BuildClipboardSubMenu(ToolStripMenuItem parent, AppSettings settings)
    {
        var items = parent.DropDownItems;
        if (Clips is null) return;
        _textGlyph ??= IconFactory.DrawTextGlyph();

        var all = Clips.Snapshot();
        var pinned = all.Where(c => c.Pinned).Take(AppSettings.MaxMenuRows).ToList();
        int clipLimit = Math.Max(0, Math.Min(settings.ClipboardMenuCount, AppSettings.MaxMenuRows - pinned.Count));
        var recent = all.Where(c => !c.Pinned).Take(clipLimit).ToList();

        foreach (var c in pinned) items.Add(MakeClipRow(c, settings, pinnedMark: true));
        if (pinned.Count > 0) items.Add(new ToolStripSeparator());
        if (recent.Count == 0) items.Add(new ToolStripMenuItem("（履歴なし）") { Enabled = false });
        foreach (var c in recent) items.Add(MakeClipRow(c, settings, pinnedMark: false));

        items.Add(new ToolStripSeparator());
        items.Add($"すべて表示（検索）... {all.Count} 件", null, (_, _) => OpenClipBrowser());
        items.Add(new ToolStripSeparator());
        int nText = all.Count(c => c.Kind == ClipKind.Text && !c.Pinned);
        int nImg = all.Count(c => c.Kind == ClipKind.Image && !c.Pinned);
        items.Add(MakeClearRow(parent, $"すべてクリア（文字 {nText}・画像 {nImg}）", null));
        items.Add(MakeClearRow(parent, $"画像のみクリア（{nImg}）", ClipKind.Image));
        items.Add(MakeClearRow(parent, $"文字のみクリア（{nText}）", ClipKind.Text));
        items.Add(new ToolStripSeparator());
        items.Add("クリップボードの設定...", null, (_, _) => OpenClipboardSettingsRequested?.Invoke());
        if (Clips.ArchiveEnabled)
        {
            items.Add("書き出し先フォルダを開く", null, (_, _) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Clips.ArchiveFolderPath);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Clips.ArchiveFolderPath + "\"") { UseShellExecute = true });
                }
                catch (Exception ex) { ShowError(ex.Message); }
            });
        }
    }

    private ToolStripMenuItem MakeClipRow(ClipItem c, AppSettings settings, bool pinnedMark)
    {
        string label = Truncate(c.Label, settings.MaxLabelLength + 10);
        var mi = new ToolStripMenuItem(Escape((pinnedMark ? "★ " : "") + label))
        {
            Tag = new ClipRef(c),
            ToolTipText = c.ToolTip,
            Image = c.Kind == ClipKind.Image ? Clips?.Thumbnail(c) : _textGlyph,
        };
        mi.Click += (_, _) => CopyClipBack(c);
        return mi;
    }

    /// <summary>クリア行。クリックしてもメニューは閉じず、一覧を更新する（ピン留めは残る）</summary>
    private ToolStripMenuItem MakeClearRow(ToolStripMenuItem parent, string text, ClipKind? kind)
    {
        var mi = new ToolStripMenuItem(text);
        mi.Tag = new InlineAction(mi, true); // KeepOpenFilter が「開いたまま」と判定する目印
        mi.Click += (_, _) =>
        {
            int n = Clips?.Clear(kind) ?? 0;
            ClipsChanged?.Invoke();
            Notify?.Invoke($"クリップボード履歴を {n} 件消しました。");
            RebuildSubMenu(parent);
        };
        return mi;
    }

    private void CopyClipBack(ClipItem c)
    {
        if (Clipboard is null) return;
        if (!Clipboard.CopyToClipboard(c))
            ShowError("クリップボードへ戻せませんでした。");
    }

    private void ShowClipActions(ToolStripMenuItem mi, ToolStripMenuItem parent, ClipRef c)
    {
        var item = c.Item;
        var defs = new List<ActionDef>
        {
            new("クリップボードにコピー", () => CopyClipBack(item), false),
            new(item.Pinned ? "ピン留めを解除" : "ピン留め", () => { Clips?.SetPinned(item, !item.Pinned); ClipsChanged?.Invoke(); }, true),
            new("削除", () => { Clips?.Remove(item); ClipsChanged?.Invoke(); }, true),
        };
        if (item.Kind == ClipKind.Image)
        {
            defs.Add(new("画像をファイルに保存...", () =>
            {
                using var dlg = new SaveFileDialog { Filter = "PNG 画像|*.png", FileName = $"clip_{item.When:yyyyMMdd_HHmmss}.png" };
                if (dlg.ShowDialog() == DialogResult.OK && Clips is not null)
                {
                    try { System.IO.File.Copy(Clips.ImagePath(item), dlg.FileName, overwrite: true); }
                    catch (Exception ex) { ShowError(ex.Message); }
                }
            }, false));
        }
        else
        {
            defs.Add(new("メモ帳で開く", () =>
            {
                try
                {
                    string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lunchbox_clip_{item.When:yyyyMMdd_HHmmss}.txt");
                    System.IO.File.WriteAllText(tmp, item.Text ?? "");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + tmp + "\"") { UseShellExecute = true });
                }
                catch (Exception ex) { ShowError(ex.Message); }
            }, false));
        }
        ShowInlineRows(mi, parent, defs);
    }

    /// <summary>サブメニューを（開いたまま）作り直す。閉じ処理が終わってから行う</summary>
    private void RebuildSubMenu(ToolStripMenuItem parent)
    {
        _menu.BeginInvoke(new Action(() =>
        {
            if (parent.IsDisposed) return;
            DisposeItems(parent.DropDownItems);
            if (parent.Tag is AppEntry app) BuildAppSubMenu(parent, app, _getSettings());
            else if (parent.Tag is ClipboardGroup) BuildClipboardSubMenu(parent, _getSettings());
            parent.DropDown.PerformLayout();
        }));
    }

    private void BuildAppSubMenu(ToolStripMenuItem appItem, AppEntry app, AppSettings settings)
    {
        var items = appItem.DropDownItems;

        // ピン留め（ピン留め＋最近 で最大 MaxMenuRows 行）
        if (app.Pins.Count == 0)
        {
            items.Add(new ToolStripMenuItem("（ピン留めなし）") { Enabled = false });
        }
        foreach (var pin in app.Pins.Take(AppSettings.MaxMenuRows))
        {
            var mi = new ToolStripMenuItem(Escape(Truncate(pin.DisplayLabel, settings.MaxLabelLength)))
            {
                Tag = new ItemRef(app, pin, null),
                ToolTipText = BuildToolTip(pin),
            };
            var img = IconCache.ForPin(pin);
            if (pin.Status == LinkStatus.NotFound)
            {
                var badged = IconCache.WithWarningBadge(img);
                _tempImages.Add(badged);
                mi.Image = badged;
            }
            else
            {
                mi.Image = img;
            }
            mi.Click += (_, _) =>
            {
                OpenPin(app, pin);
            };
            items.Add(mi);
        }

        items.Add(new ToolStripSeparator());

        // 最近（表示件数は設定値。ただしピン留めと合わせて MaxMenuRows 行まで）
        int recentLimit = Math.Max(0, Math.Min(settings.RecentCount, AppSettings.MaxMenuRows - app.Pins.Count));
        var recents = _getHistory().Get(app.Id, recentLimit,
            app.Pins.Select(p => p.Target).Concat(app.Pins.Select(p => p.TargetUnc ?? "")));
        if (recents.Count == 0)
        {
            items.Add(new ToolStripMenuItem("（最近の履歴なし）") { Enabled = false });
        }
        foreach (var r in recents)
        {
            string label = string.IsNullOrWhiteSpace(r.Title) ? PinItem.DefaultLabel(r.Kind, r.Target) : r.Title;
            var mi = new ToolStripMenuItem(Escape(Truncate(label, settings.MaxLabelLength)))
            {
                Tag = new ItemRef(app, null, r),
                ToolTipText = r.Target + Environment.NewLine + r.When.ToString("yyyy/MM/dd HH:mm") + Environment.NewLine + "右クリックでピン留め",
                Image = IconCache.ForTarget(r.Kind, r.Target),
            };
            mi.Click += (_, _) =>
            {
                try { Launcher.OpenTarget(r.Kind, r.Target); }
                catch (Exception ex) { ShowError("開けませんでした。\n" + ex.Message); }
            };
            items.Add(mi);
        }

        items.Add(new ToolStripSeparator());
        int total = _getHistory().Count(app.Id);
        if (total > 0)
            items.Add($"すべて表示（検索）... {total} 件", null, (_, _) => OpenAppHistoryBrowser(app));
        items.Add("このアプリを起動", null, (_, _) => LaunchApp(app));
        items.Add("ピン留めの管理...", null, (_, _) => ManagePinsRequested?.Invoke(app));
    }

    // ---------- 全件表示（検索ウインドウ） ----------

    private HistoryBrowserWindow? _browser;

    private void ShowBrowser(HistoryBrowserWindow w)
    {
        try { _browser?.Close(); } catch { }
        _browser = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_browser, w)) _browser = null; };
        w.Show();
        w.Activate();
    }

    private static System.Windows.Media.ImageSource? ToSource(Bitmap? b) => IconCache.ToBitmapSource(b);

    private void OpenAppHistoryBrowser(AppEntry app)
    {
        List<HistoryBrowserWindow.Row> Load() =>
            _getHistory().Get(app.Id, int.MaxValue, app.Pins.Select(p => p.Target))
                .Select(r => new HistoryBrowserWindow.Row
                {
                    Title = string.IsNullOrWhiteSpace(r.Title) ? PinItem.DefaultLabel(r.Kind, r.Target) : r.Title,
                    Sub = r.Target,
                    When = r.When,
                    Source = r,
                    IconLoader = () => ToSource(IconCache.ForTarget(r.Kind, r.Target)),
                }).ToList();

        var actions = new List<HistoryBrowserWindow.RowAction>
        {
            new("開く", row => { if (row.Source is RecentItem r) Launcher.OpenTarget(r.Kind, r.Target); }, false),
            new("ピン留め", row => { if (row.Source is RecentItem r) PinFromRecent(app, r); }, true),
            new("履歴から消す", row => { if (row.Source is RecentItem r) { _getHistory().Remove(app.Id, r.Target); HistoryChanged?.Invoke(); } }, true),
            new("保存場所を開く", row => { if (row.Source is RecentItem r) Launcher.OpenContaining(new PinItem { Kind = r.Kind, Target = r.Target }); }, false),
            new("パスをコピー", row => { if (row.Source is RecentItem r) System.Windows.Forms.Clipboard.SetText(r.Target); }, false),
        };
        ShowBrowser(new HistoryBrowserWindow($"{app.Name} の履歴", Load, "開く",
            row => { if (row.Source is RecentItem r) Launcher.OpenTarget(r.Kind, r.Target); }, actions));
    }

    private void OpenClipBrowser()
    {
        if (Clips is null) return;
        List<HistoryBrowserWindow.Row> Load() =>
            Clips.Snapshot().Select(c => new HistoryBrowserWindow.Row
            {
                Title = (c.Pinned ? "★ " : "") + c.Label,
                Sub = c.Kind == ClipKind.Image ? $"画像 {c.Width}×{c.Height}" : $"{(c.Text ?? "").Length} 文字",
                When = c.When,
                Source = c,
                IconLoader = () => ToSource(c.Kind == ClipKind.Image ? Clips.Thumbnail(c) : (_textGlyph ??= IconFactory.DrawTextGlyph())),
            }).ToList();

        var actions = new List<HistoryBrowserWindow.RowAction>
        {
            new("クリップボードにコピー", row => { if (row.Source is ClipItem c) CopyClipBack(c); }, false),
            new("ピン留め／解除", row => { if (row.Source is ClipItem c) { Clips.SetPinned(c, !c.Pinned); ClipsChanged?.Invoke(); } }, true),
            new("削除", row => { if (row.Source is ClipItem c) { Clips.Remove(c); ClipsChanged?.Invoke(); } }, true),
        };
        ShowBrowser(new HistoryBrowserWindow("クリップボードの履歴", Load, "コピー",
            row => { if (row.Source is ClipItem c) CopyClipBack(c); }, actions));
    }

    private static string BuildToolTip(PinItem pin)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(pin.Target);
        if (!string.IsNullOrEmpty(pin.TargetUnc) && !string.Equals(pin.TargetUnc, pin.Target, StringComparison.OrdinalIgnoreCase))
            sb.Append(Environment.NewLine).Append(pin.TargetUnc);
        if (!string.IsNullOrWhiteSpace(pin.Arguments))
            sb.Append(Environment.NewLine).Append("引数: ").Append(pin.Arguments);
        if (!string.IsNullOrWhiteSpace(pin.Memo))
            sb.Append(Environment.NewLine).Append(pin.Memo);
        if (pin.Status == LinkStatus.NotFound)
            sb.Append(Environment.NewLine).Append("⚠ 見つかりません（右クリック→編集 で再リンク）");
        else if (pin.Status == LinkStatus.Relinked)
            sb.Append(Environment.NewLine).Append("移動先を自動で追跡しました");
        sb.Append(Environment.NewLine).Append("右クリックで編集・解除など");
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, Math.Max(1, max - 1)) + "…";
    }

    private static string Escape(string s) => s.Replace("&", "&&");

    /// <summary>
    /// 項目を取り除いて破棄する。Clear() だけだと項目とサブメニュー（ウインドウを持つ）が残り続けるため。
    /// 画像はキャッシュと共有なので破棄しない（Image は ToolStripItem.Dispose では破棄されない）。
    /// </summary>
    private void DisposeItems(ToolStripItemCollection items)
    {
        // その場では破棄しない。クリック処理の途中で自分自身の項目を破棄すると、
        // 処理が戻った先で破棄済みの項目に触れて落ちる（11/11 の不正終了）。
        // いったん外して溜めておき、メニューが閉じている次の作り直しのときにまとめて破棄する。
        if (!_menu.Visible) FlushPendingDispose();
        _pendingDispose.AddRange(items.Cast<ToolStripItem>());
        items.Clear();
    }

    private static readonly List<ToolStripItem> _pendingDispose = new();

    private void FlushPendingDispose()
    {
        var old = _pendingDispose.ToList();
        _pendingDispose.Clear();
        foreach (var it in old) DisposeNow(it);
    }

    private static void DisposeNow(ToolStripItem it)
    {
        try
        {
            if (it.IsDisposed) return;
            if (it is ToolStripDropDownItem ddi && ddi.HasDropDown)
            {
                var dd = ddi.DropDown;
                foreach (var c in dd.Items.Cast<ToolStripItem>().ToList()) { dd.Items.Remove(c); DisposeNow(c); }
                it.Image = null;
                it.Dispose();
                if (!dd.IsDisposed) dd.Dispose();
            }
            else
            {
                it.Image = null;
                it.Dispose();
            }
        }
        catch (Exception ex) { Logger.Log("メニュー項目の破棄に失敗: " + ex.Message); }
    }

    private void DisposeTempImages()
    {
        foreach (var b in _tempImages) b.Dispose();
        _tempImages.Clear();
    }

    // ---------- 開いたままにする制御 ----------

    /// <summary>操作行（ピン留め／解除／履歴から消す等）をクリックしたとき、_keepOpenOnce が立っていれば閉じない</summary>
    private void KeepOpenFilter(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        // WinForms は項目の Click を呼ぶ前にメニューを閉じるので、閉じる時点で「選択中の項目」を見て判断する
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && sender is ToolStripDropDown dd)
        {
            var selected = dd.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Selected);
            if (selected?.Tag is InlineAction a && a.KeepOpen)
            {
                KeepOpenForThisClick();
                e.Cancel = true;
                return;
            }
        }
        if (_keepOpenOnce &&
            (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked ||
             e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange ||
             e.CloseReason == ToolStripDropDownCloseReason.AppClicked))
            e.Cancel = true;
    }

    /// <summary>次のクリックによる閉じ処理を1回だけ抑止し、少し後に解除する</summary>
    private void KeepOpenForThisClick()
    {
        _keepOpenOnce = true;
        _menu.BeginInvoke(new Action(() => _keepOpenOnce = false));
    }

    // ---------- 右クリック（操作行の差し込み） ----------

    /// <summary>操作行の目印（Tag）</summary>
    private sealed record InlineAction(ToolStripMenuItem Owner, bool KeepOpen);

    private sealed record ActionDef(string Text, Action Run, bool KeepOpen);

    private void ShowItemActions(ToolStripMenuItem mi, ToolStripMenuItem appItem, ItemRef r) =>
        ShowInlineRows(mi, appItem, r.Pin is not null ? PinActions(appItem, r.App, r.Pin) : RecentActions(appItem, r.App, r.Recent!));

    /// <summary>右クリックした項目の直下に操作行を差し込む（同じ項目をもう一度右クリックで畳む）</summary>
    private void ShowInlineRows(ToolStripMenuItem mi, ToolStripMenuItem appItem, List<ActionDef> defs)
    {
        var items = appItem.DropDownItems;
        bool wasExpanded = items.OfType<ToolStripMenuItem>().Any(x => x.Tag is InlineAction a && ReferenceEquals(a.Owner, mi) && !ReferenceEquals(a.Owner, x));
        CollapseInlineActions(appItem);
        if (wasExpanded) return;

        int index = items.IndexOf(mi);
        if (index < 0) return;

        int pos = index + 1;
        foreach (var d in defs)
        {
            var row = new ToolStripMenuItem("　└ " + d.Text) { Tag = new InlineAction(mi, d.KeepOpen) };
            var def = d;
            row.Click += (_, _) =>
            {
                if (def.KeepOpen)
                {
                    try { def.Run(); }
                    catch (Exception ex) { ShowError(ex.Message); }
                    RebuildSubMenu(appItem);
                }
                else
                {
                    def.Run();
                }
            };
            items.Insert(pos++, row);
        }
        appItem.DropDown.PerformLayout();
    }

    private static void CollapseInlineActions(ToolStripMenuItem appItem)
    {
        var rows = appItem.DropDownItems.OfType<ToolStripMenuItem>().Where(x => x.Tag is InlineAction a && !ReferenceEquals(a.Owner, x)).ToList();
        foreach (var x in rows) { appItem.DropDownItems.Remove(x); _pendingDispose.Add(x); }
    }

    private List<ActionDef> PinActions(ToolStripMenuItem appItem, AppEntry app, PinItem pin)
    {
        var list = new List<ActionDef>
        {
            new("開く", () => OpenPin(app, pin), false),
            new("ピン留めを解除", () => { app.Pins.Remove(pin); SettingsChanged?.Invoke(); }, true),
            new("編集...", () => EditPinRequested?.Invoke(app, pin), false),
        };
        if (app.Pins.Count > 1)
        {
            list.Add(new("上へ", () => MovePin(app, pin, -1), true));
            list.Add(new("下へ", () => MovePin(app, pin, +1), true));
        }
        list.Add(new("保存場所を開く", () =>
        {
            try { Launcher.OpenContaining(pin); }
            catch (Exception ex) { ShowError(ex.Message); }
        }, false));
        list.Add(new("パスをコピー", () => { Launcher.CopyPath(pin); Notify?.Invoke("パスをコピーしました。"); }, true));
        if (pin.Kind == ItemKind.Url)
            list.Add(new("アイコンを取り直す", () => IconCache.RefreshFavicon(pin.Target), true));
        return list;
    }

    private List<ActionDef> RecentActions(ToolStripMenuItem appItem, AppEntry app, RecentItem rec)
    {
        return new List<ActionDef>
        {
            new("開く", () =>
            {
                try { Launcher.OpenTarget(rec.Kind, rec.Target); }
                catch (Exception ex) { ShowError(ex.Message); }
            }, false),
            new("ピン留め", () => PinFromRecent(app, rec), true),
            new("履歴から消す", () => { _getHistory().Remove(app.Id, rec.Target); HistoryChanged?.Invoke(); }, true),
            new("保存場所を開く", () =>
            {
                try { Launcher.OpenContaining(new PinItem { Kind = rec.Kind, Target = rec.Target }); }
                catch (Exception ex) { ShowError(ex.Message); }
            }, false),
            new("パスをコピー", () =>
            {
                try { System.Windows.Forms.Clipboard.SetText(rec.Target); Notify?.Invoke("パスをコピーしました。"); } catch { }
            }, true),
        };
    }

    /// <summary>アプリを別のアプリの上に落としたときの並べ替え。下へ落とせばその下、上へ落とせばその上に入る</summary>
    private void ReorderAppsByDrop(ToolStripMenuItem src, ToolStripMenuItem dst, bool after)
    {
        if (src.Tag is not AppEntry a || dst.Tag is not AppEntry b || ReferenceEquals(a, b)) return;
        var apps = _getSettings().Apps;
        if (!apps.Remove(a)) return;
        int to = apps.IndexOf(b);
        if (to < 0) { apps.Add(a); return; }
        apps.Insert(after ? to + 1 : to, a);
        SettingsChanged?.Invoke();
        // メニューを開いたまま並び直す
        _menu.BeginInvoke(new Action(() =>
        {
            Rebuild();
            _menu.PerformLayout();
        }));
    }

    /// <summary>ピン留め項目を別のピン留め項目の上に落としたときの並べ替え。下へ落とせばその下、上へ落とせばその上に入る</summary>
    private void ReorderPinsByDrop(ToolStripMenuItem appItem, ToolStripMenuItem src, ToolStripMenuItem dst, bool after)
    {
        if (src.Tag is not ItemRef a || a.Pin is null) return;
        if (dst.Tag is not ItemRef b || b.Pin is null) return;
        if (!ReferenceEquals(a.App, b.App) || ReferenceEquals(a.Pin, b.Pin)) return;
        var pins = a.App.Pins;
        if (!pins.Remove(a.Pin)) return;
        int to = pins.IndexOf(b.Pin);
        if (to < 0) { pins.Add(a.Pin); return; }
        pins.Insert(after ? to + 1 : to, a.Pin); // カーソルが対象の下半分なら下、上半分なら上に入る
        SettingsChanged?.Invoke();
        RebuildSubMenu(appItem);
    }

    private static void MovePin(AppEntry app, PinItem pin, int delta)
    {
        int i = app.Pins.IndexOf(pin);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= app.Pins.Count) return;
        (app.Pins[i], app.Pins[j]) = (app.Pins[j], app.Pins[i]);
    }


    // ---------- 動作 ----------

    private void LaunchApp(AppEntry app)
    {
        try { Launcher.OpenApp(app); }
        catch (Exception ex) { ShowError($"{app.Name} を起動できませんでした。\n{ex.Message}"); }
    }

    private void OpenPin(AppEntry app, PinItem pin)
    {
        try
        {
            if (pin.Kind != ItemKind.Url && !PinItem.PathExists(pin.Target) && !PinItem.PathExists(pin.TargetUnc))
            {
                var status = LinkResolver.Check(pin);
                SettingsChanged?.Invoke();
                if (status == LinkStatus.NotFound)
                {
                    var r = MessageBox.Show(
                        $"「{pin.DisplayLabel}」が見つかりません。\n{pin.Target}\n\n編集して場所を指定し直しますか？",
                        AppSettings.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                        EditPinRequested?.Invoke(app, pin);
                    return;
                }
                if (status == LinkStatus.Relinked)
                    Notify?.Invoke($"移動先を見つけました：{pin.Target}");
            }
            Launcher.OpenPin(pin);
        }
        catch (Exception ex)
        {
            ShowError($"開けませんでした。\n{ex.Message}");
        }
    }

    private void PinFromRecent(AppEntry app, RecentItem rec)
    {
        if (app.Pins.Any(p => string.Equals(p.Target, rec.Target, StringComparison.OrdinalIgnoreCase)))
            return;
        var pin = new PinItem
        {
            Kind = rec.Kind,
            Target = rec.Target,
            Label = rec.Kind == ItemKind.Url ? (rec.Title ?? "") : "",
        };
        LinkResolver.CaptureIdentity(pin);
        app.Pins.Add(pin);
        SettingsChanged?.Invoke();
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, AppSettings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public void Dispose()
    {
        DisposeTempImages();
        _menu.Dispose();
    }
}

/// <summary>
/// メニュー項目のドラッグ＆ドロップ並べ替え（メインメニューとサブメニューで共用）。
/// WM_LBUTTONDOWN／MOUSEMOVE／LBUTTONUP を見て自前で判定し、ドロップ時は WM_LBUTTONUP を握りつぶして
/// クリック扱いにしない（メニューも閉じない）。OLE の DoDragDrop はメニューのモーダル管理と衝突するので使わない。
/// </summary>
internal sealed class ToolStripDragReorder
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;

    private readonly ToolStrip _strip;
    private ToolStripMenuItem? _pressedItem;
    private Point _pressedPoint;
    private bool _dragging;
    // 挿入線の表示用：いま乗っている項目と、その上／下どちらに入るか
    private ToolStripMenuItem? _hoverTarget;
    private bool _hoverAfter;

    /// <summary>ドラッグを許す項目の判定（未設定なら無効）</summary>
    public Func<ToolStripMenuItem, bool>? CanDrag { get; set; }
    /// <summary>項目を落としたとき（元, 先, 先の下に入れるなら true）</summary>
    public event Action<ToolStripMenuItem, ToolStripMenuItem, bool>? ItemDropped;

    /// <summary>カーソル位置から「どの項目の上／下に入るか」を決める</summary>
    private (ToolStripMenuItem? target, bool after) Locate(Point pt)
    {
        var over = _strip.GetItemAt(pt) as ToolStripMenuItem;
        if (over is null || over == _pressedItem || !(CanDrag?.Invoke(over) ?? false)) return (null, false);
        bool after = pt.Y > over.Bounds.Top + over.Bounds.Height / 2;
        return (over, after);
    }

    private void SetHover(ToolStripMenuItem? target, bool after)
    {
        if (ReferenceEquals(_hoverTarget, target) && _hoverAfter == after) return;
        _hoverTarget = target;
        _hoverAfter = after;
        _strip.Invalidate();
    }

    /// <summary>挿入線を描く（派生クラスの OnPaint の最後で呼ぶ）</summary>
    public void Paint(PaintEventArgs e)
    {
        if (!_dragging || _hoverTarget is null) return;
        var b = _hoverTarget.Bounds;
        int y = _hoverAfter ? b.Bottom - 1 : b.Top;
        using var pen = new Pen(SystemColors.Highlight, 2f);
        using var brush = new SolidBrush(SystemColors.Highlight);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLine(pen, b.Left + 4, y, b.Right - 4, y);
        // 両端に小さな三角で「ここ」を示す
        e.Graphics.FillPolygon(brush, new[] { new Point(b.Left + 1, y - 4), new Point(b.Left + 1, y + 4), new Point(b.Left + 7, y) });
        e.Graphics.FillPolygon(brush, new[] { new Point(b.Right - 1, y - 4), new Point(b.Right - 1, y + 4), new Point(b.Right - 7, y) });
    }

    public ToolStripDragReorder(ToolStrip strip) { _strip = strip; }

    public static Point PointFromLParam(IntPtr lParam)
    {
        int lp = lParam.ToInt32();
        return new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
    }

    /// <summary>true を返したらメッセージを握りつぶす</summary>
    public bool Handle(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONDOWN:
            {
                var pt = PointFromLParam(m.LParam);
                _pressedItem = _strip.GetItemAt(pt) as ToolStripMenuItem;
                if (_pressedItem is not null && !(CanDrag?.Invoke(_pressedItem) ?? false))
                    _pressedItem = null;
                _pressedPoint = pt;
                _dragging = false;
                return false;
            }
            case WM_MOUSEMOVE:
            {
                if (_pressedItem is null || (m.WParam.ToInt32() & 0x0001) == 0) return false;
                var pt = PointFromLParam(m.LParam);
                if (!_dragging)
                {
                    var ds = SystemInformation.DragSize;
                    if (Math.Abs(pt.X - _pressedPoint.X) < ds.Width / 2 && Math.Abs(pt.Y - _pressedPoint.Y) < ds.Height / 2)
                        return false;
                    _dragging = true;
                    // 開いているサブメニューは閉じておく
                    foreach (var dd in _strip.Items.OfType<ToolStripDropDownItem>())
                        if (dd.HasDropDown && dd.DropDown.Visible) dd.HideDropDown();
                }
                var (over, after) = Locate(pt);
                SetHover(over, after);
                Cursor.Current = over is not null ? Cursors.SizeAll : Cursors.No;
                // ドラッグ中は WinForms にマウス移動を渡さない（選択の移動やサブメニューの展開を止め、挿入線だけを出す）
                return true;
            }
            case WM_LBUTTONUP:
            {
                if (!_dragging || _pressedItem is null)
                {
                    _pressedItem = null;
                    return false;
                }
                var pt = PointFromLParam(m.LParam);
                var (target, after) = Locate(pt);
                var src = _pressedItem;
                _pressedItem = null;
                _dragging = false;
                SetHover(null, false);
                Cursor.Current = Cursors.Default;
                if (target is not null)
                    ItemDropped?.Invoke(src, target, after);
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// 右ボタンのメッセージを横取りするサブメニュー。
/// WinForms は項目上の右ボタン離しも「項目クリック」として扱いメニューを閉じるので、
/// ここで WM_RBUTTON* を握りつぶし、代わりに ItemRightClicked を発火する。ドラッグ並べ替えも持つ。
/// </summary>
internal sealed class RightClickDropDownMenu : ToolStripDropDownMenu
{
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_RBUTTONDBLCLK = 0x0206;

    private readonly ToolStripDragReorder _drag;

    public event Action<ToolStripMenuItem>? ItemRightClicked;

    public Func<ToolStripMenuItem, bool>? CanDrag { get => _drag.CanDrag; set => _drag.CanDrag = value; }
    public event Action<ToolStripMenuItem, ToolStripMenuItem, bool>? ItemDropped
    {
        add => _drag.ItemDropped += value;
        remove => _drag.ItemDropped -= value;
    }

    public RightClickDropDownMenu() { _drag = new ToolStripDragReorder(this); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _drag.Paint(e);
    }

    /// <summary>キーボード：アプリケーションキー／Shift+F10 でも選択中の項目の操作行を開く</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Apps || keyData == (Keys.Shift | Keys.F10))
        {
            var selected = Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Selected && i.Enabled);
            if (selected is not null)
            {
                ItemRightClicked?.Invoke(selected);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        if (_drag.Handle(ref m))
            return;
        if (m.Msg == WM_RBUTTONDOWN || m.Msg == WM_RBUTTONDBLCLK)
            return;
        if (m.Msg == WM_RBUTTONUP)
        {
            var pt = ToolStripDragReorder.PointFromLParam(m.LParam);
            if (GetItemAt(pt) is ToolStripMenuItem mi && mi.Enabled)
                ItemRightClicked?.Invoke(mi);
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// トップ階層（メインメニュー）用。右ボタンを横取りして「項目クリック」扱いにさせない。
/// 直接起動の項目を右クリックしても起動しないようにするため。アプリのドラッグ並べ替えも持つ。
/// </summary>
internal sealed class RightClickContextMenuStrip : ContextMenuStrip
{
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_RBUTTONDBLCLK = 0x0206;

    private readonly ToolStripDragReorder _drag;

    public event Action<ToolStripMenuItem>? ItemRightClicked;

    public Func<ToolStripMenuItem, bool>? CanDrag { get => _drag.CanDrag; set => _drag.CanDrag = value; }
    public event Action<ToolStripMenuItem, ToolStripMenuItem, bool>? ItemDropped
    {
        add => _drag.ItemDropped += value;
        remove => _drag.ItemDropped -= value;
    }

    public RightClickContextMenuStrip() { _drag = new ToolStripDragReorder(this); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _drag.Paint(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (_drag.Handle(ref m))
            return;
        if (m.Msg == WM_RBUTTONDOWN || m.Msg == WM_RBUTTONDBLCLK)
            return;
        if (m.Msg == WM_RBUTTONUP)
        {
            var pt = ToolStripDragReorder.PointFromLParam(m.LParam);
            if (GetItemAt(pt) is ToolStripMenuItem mi && mi.Enabled)
                ItemRightClicked?.Invoke(mi);
            return;
        }
        base.WndProc(ref m);
    }
}
