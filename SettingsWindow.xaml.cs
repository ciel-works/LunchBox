using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using Cursors = System.Windows.Input.Cursors;
using ListViewItem = System.Windows.Controls.ListViewItem;
using ListBoxItem = System.Windows.Controls.ListBoxItem;

namespace LunchBox;

/// <summary>設定画面（アプリ／ピン留め／全般）。保存するまで元の設定には触れない</summary>
public partial class SettingsWindow : Window
{
    private sealed class AppRow
    {
        public AppEntry App { get; }
        public string Name => App.Name;
        public ImageSource? Icon { get; set; }
        public AppRow(AppEntry app) { App = app; Icon = IconCache.ToBitmapSource(IconCache.ForApp(app)); }
    }

    private sealed class PinRow
    {
        public PinItem Pin { get; }
        public ImageSource? Icon { get; }
        public string Label => Pin.DisplayLabel;
        public string KindText => Pin.Kind switch { ItemKind.Folder => "フォルダ", ItemKind.Url => "URL", _ => "ファイル" };
        public string Target => Pin.Target;
        public string StatusText => Pin.Status switch
        {
            LinkStatus.Ok => "OK",
            LinkStatus.Relinked => "追跡済み",
            LinkStatus.NotFound => "⚠ 見つかりません",
            _ => "未確認",
        };
        public PinRow(PinItem pin) { Pin = pin; Icon = IconCache.ToBitmapSource(IconCache.ForPin(pin)); }
    }

    private readonly AppSettings _settings;
    private readonly ObservableCollection<AppRow> _apps = new();
    private readonly ObservableCollection<PinRow> _pins = new();
    private AppEntry? _currentApp;
    private bool _loadingApp;
    private uint _pendingModifiers;
    private uint _pendingVk;

    public event Action<AppSettings>? SettingsSaved;
    /// <summary>「切替の設定...」が押された（切替の設定は別ウインドウで、即時に保存される）</summary>
    public event Action? OpenSwitcherSettingsRequested;

    private void OpenSwitcherSettings_Click(object sender, RoutedEventArgs e) => OpenSwitcherSettingsRequested?.Invoke();
    /// <summary>履歴のクリア要求（appId が null なら全アプリ）。保存を待たずすぐ実行される</summary>
    public event Action<string?>? ClearHistoryRequested;
    /// <summary>クリップボード履歴のクリア要求（null＝すべて）。すぐ実行される</summary>
    public event Action<ClipKind?>? ClearClipsRequested;
    /// <summary>現在のクリップボード履歴の件数（文字, 画像）を返す。App が設定する</summary>
    public Func<(int Text, int Images)>? GetClipCounts { get; set; }

    public SettingsWindow(AppSettings settings, string? selectAppId)
    {
        InitializeComponent();
        _settings = settings;
        foreach (var a in settings.Apps) _apps.Add(new AppRow(a));
        AppList.ItemsSource = _apps;
        PinAppList.ItemsSource = _apps;
        PinList.ItemsSource = _pins;

        // 全般
        HotKeyEnabledBox.IsChecked = settings.HotKeyEnabled;
        HotKeyTargetBox.SelectedIndex = settings.HotKeyOpensSearch ? 0 : 1;
        TrayClickBox.SelectedIndex = settings.SwapTrayClicks ? 1 : 0;
        _pendingModifiers = settings.HotKeyModifiers;
        _pendingVk = settings.HotKeyVirtualKey;
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);

        NotifyEnabledBox.IsChecked = settings.NotifyPlaceEnabled;
        NotifyModeBox.SelectedIndex = Math.Clamp(settings.NotifyPlaceMode, 0, 2);
        var notifyMons = NotificationPlacer.ListMonitors().Select(m => new NotifyMonitorItem(m.Key, m.Label)).ToList();
        if (settings.NotifyPlaceMonitor.Length > 0 && !notifyMons.Any(m => m.Key == settings.NotifyPlaceMonitor))
            notifyMons.Add(new NotifyMonitorItem(settings.NotifyPlaceMonitor, "（いまはつながっていないモニタ）"));
        NotifyMonitorBox.ItemsSource = notifyMons;
        // 未設定なら、メイン以外で一番右のモニタを選んでおく
        NotifyMonitorBox.SelectedItem = notifyMons.FirstOrDefault(m => m.Key == settings.NotifyPlaceMonitor)
                                        ?? notifyMons.LastOrDefault(m => !m.Label.Contains("メイン")) ?? notifyMons.FirstOrDefault();
        NotifyMonitorBox.IsEnabled = NotifyModeBox.SelectedIndex == 0;
        string notifyStatus = (System.Windows.Application.Current as App)?.NotifyPlaceStatus ?? "";
        NotifyStatusText.Text = notifyStatus.Length > 0 ? "いまの状態：" + notifyStatus : "";

        CalEnabledBox.IsChecked = settings.CalendarEnabled;
        _calKey = (settings.CalendarHotKeyModifiers, settings.CalendarHotKeyVirtualKey);
        CalKeyBox.Text = LayoutKeyText(_calKey);
        CalMonitorBox.SelectedIndex = Math.Clamp(settings.CalendarMonitor, 0, 3);
        CalWeekStartBox.SelectedIndex = settings.CalendarWeekStartsMonday ? 1 : 0;
        CalUpdateOnStartupBox.IsChecked = settings.CalendarHolidayUpdateOnStartup;
        CalAutoUpdateBox.IsChecked = settings.CalendarHolidayAutoUpdate;
        CalProvisionalBox.SelectedIndex = settings.CalendarShowProvisional ? 0 : 1;
        CalCompanyBox.Text = settings.CalendarCompanyHolidays;
        CalAddsBox.Text = settings.CalendarHolidayAdds;
        CalRemovesBox.Text = settings.CalendarHolidayRemoves;
        CalKeyStatusText.Text = (System.Windows.Application.Current as App)?.CalendarHotKeyStatus ?? "";
        RefreshCalendarStatus();
        RefreshCalendarCounts();

        LayoutAutoRecordBox.IsChecked = settings.LayoutAutoRecord;
        LayoutAutoRestoreBox.SelectedIndex = Math.Clamp(settings.LayoutAutoRestoreMode, 0, 2);
        LayoutLaunchMissingBox.IsChecked = settings.LayoutLaunchMissing;
        _layRestoreKey = (settings.LayoutRestoreHotKeyModifiers, settings.LayoutRestoreHotKeyVirtualKey);
        _layRecallKey = (settings.LayoutRecallHotKeyModifiers, settings.LayoutRecallHotKeyVirtualKey);
        LayoutRestoreKeyBox.Text = LayoutKeyText(_layRestoreKey);
        LayoutRecallKeyBox.Text = LayoutKeyText(_layRecallKey);
        RecentCountBox.Text = settings.RecentCount.ToString();
        HistoryMaxBox.Text = settings.HistoryMaxPerApp.ToString();
        MaxLabelBox.Text = settings.MaxLabelLength.ToString();
        ChromeProfileBox.Text = settings.ChromeProfile;
        StartupBox.IsChecked = settings.StartWithWindows;
        RunElevatedBox.IsChecked = settings.RunElevated;
        SwitcherEnabledBox.IsChecked = settings.SwitcherEnabled;
        SelectLinkCheck(settings.LinkCheckIntervalMinutes);
        // クリップボード
        ClipEnabledBox.IsChecked = settings.ClipboardEnabled;
        ClipImagesBox.IsChecked = settings.ClipboardKeepImages;
        ClipMaxTextBox.Text = settings.ClipboardMaxText.ToString();
        ClipMaxImagesBox.Text = settings.ClipboardMaxImages.ToString();
        ClipMenuCountBox.Text = settings.ClipboardMenuCount.ToString();
        ClipPersistBox.IsChecked = settings.ClipboardPersist;
        ClipArchiveBox.IsChecked = settings.ClipboardArchiveOverflow;
        ClipArchiveFolderBox.Text = settings.ClipboardArchiveFolder ?? "";
        Loaded += (_, _) => UpdateClipCount();
        PathsText.Text = $"設定ファイル: {AppSettings.SettingsPath}\nログ: {Path.Combine(AppSettings.SettingsDirectory, "log.txt")}";

        if (_apps.Count > 0) AppList.SelectedIndex = 0;
        SelectApp(selectAppId);
    }

    /// <summary>タブをヘッダー名で選ぶ</summary>
    public void SelectTab(string header)
    {
        foreach (TabItem t in Tabs.Items)
            if (t.Header as string == header) { Tabs.SelectedItem = t; return; }
    }

    private void UpdateClipCount()
    {
        if (GetClipCounts is null) { ClipCountText.Text = ""; return; }
        var (t, i) = GetClipCounts();
        ClipCountText.Text = $"現在：文字 {t} 件／画像 {i} 件";
    }

    private void BrowseClipArchive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "書き出し先フォルダを選択" };
        try { if (Directory.Exists(ClipArchiveFolderBox.Text)) dlg.InitialDirectory = ClipArchiveFolderBox.Text; } catch { }
        if (dlg.ShowDialog(this) == true) ClipArchiveFolderBox.Text = dlg.FolderName;
    }

    private void OpenClipArchive_Click(object sender, RoutedEventArgs e)
    {
        string folder = string.IsNullOrWhiteSpace(ClipArchiveFolderBox.Text) ? ClipStore.DefaultArchiveFolder : ClipArchiveFolderBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearClipsAll_Click(object sender, RoutedEventArgs e) { ClearClipsRequested?.Invoke(null); UpdateClipCount(); }
    private void ClearClipsImages_Click(object sender, RoutedEventArgs e) { ClearClipsRequested?.Invoke(ClipKind.Image); UpdateClipCount(); }
    private void ClearClipsText_Click(object sender, RoutedEventArgs e) { ClearClipsRequested?.Invoke(ClipKind.Text); UpdateClipCount(); }

    /// <summary>ピン留めタブで指定アプリを選ぶ（トレイの「ピン留めの管理」から）</summary>
    public void SelectApp(string? appId)
    {
        if (appId is null)
        {
            if (PinAppList.SelectedIndex < 0 && _apps.Count > 0) PinAppList.SelectedIndex = 0;
            return;
        }
        var row = _apps.FirstOrDefault(r => r.App.Id == appId);
        if (row is null) return;
        Tabs.SelectedItem = PinsTab;
        PinAppList.SelectedItem = row;
    }

    // ===================== アプリ =====================

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentApp = (AppList.SelectedItem as AppRow)?.App;
        AppDetail.IsEnabled = _currentApp is not null;
        _loadingApp = true;
        AppNameBox.Text = _currentApp?.Name ?? "";
        AppExeBox.Text = _currentApp?.ExePath ?? "";
        AppSourceBox.SelectedIndex = _currentApp is null ? 0 : (int)_currentApp.Source;
        AppAumidBox.Text = _currentApp?.AppUserModelId ?? "";
        DirectLaunchBox.IsChecked = _currentApp?.DirectLaunch ?? false;
        RunAsAdminBox.IsChecked = _currentApp?.RunAsAdmin ?? false;
        RunAsAdminBox.IsEnabled = _currentApp is not null && !_currentApp.IsShellApp;
        _loadingApp = false;
        UpdateAppIconPreview();
    }

    private void UpdateAppIconPreview()
    {
        AppIconPreview.Source = _currentApp is null ? null : IconCache.ToBitmapSource(IconCache.ForApp(_currentApp));
    }

    private void RefreshAppRows()
    {
        for (int i = 0; i < _apps.Count; i++)
            _apps[i].Icon = IconCache.ToBitmapSource(IconCache.ForApp(_apps[i].App));
        AppList.Items.Refresh();
        PinAppList.Items.Refresh();
    }

    private void AppNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingApp || _currentApp is null) return;
        _currentApp.Name = AppNameBox.Text;
        AppList.Items.Refresh();
        PinAppList.Items.Refresh();
    }

    private void AppExeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingApp || _currentApp is null) return;
        _currentApp.ExePath = AppExeBox.Text.Trim().Trim('"');
        RunAsAdminBox.IsEnabled = !_currentApp.IsShellApp;
        UpdateAppIconPreview();
        RefreshAppRows();
    }

    private void AppSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingApp || _currentApp is null) return;
        _currentApp.Source = (HistorySource)Math.Max(0, AppSourceBox.SelectedIndex);
    }

    private void ClearAppHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentApp is null) return;
        if (MessageBox.Show($"「{_currentApp.Name}」の「最近」をすべて消します。よろしいですか？\n（収集元に残っている分は次の収集で再び表示されます）",
                AppSettings.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ClearHistoryRequested?.Invoke(_currentApp.Id);
    }

    private void ClearAllHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("すべてのアプリの「最近」を消します。よろしいですか？\n（収集元に残っている分は次の収集で再び表示されます）",
                AppSettings.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ClearHistoryRequested?.Invoke(null);
    }

    private void RunAsAdminBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingApp || _currentApp is null) return;
        _currentApp.RunAsAdmin = RunAsAdminBox.IsChecked == true;
    }

    private void AddFromAppList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AppPickerWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected is null) return;
        var entry = dlg.Selected;
        if (_apps.Any(r => string.Equals(r.App.ExePath, entry.LaunchTarget, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("そのアプリは既に登録されています。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var app = new AppEntry
        {
            Name = entry.Name,
            ExePath = entry.LaunchTarget,
            AppUserModelId = entry.Path,     // ジャンプリスト（最近使ったもの）はこの ID で探す
            Source = HistorySource.Windows,
        };
        _apps.Add(new AppRow(app));
        AppList.SelectedIndex = _apps.Count - 1;
    }

    private void DirectLaunchBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingApp || _currentApp is null) return;
        _currentApp.DirectLaunch = DirectLaunchBox.IsChecked == true;
    }

    private void AppAumidBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingApp || _currentApp is null) return;
        _currentApp.AppUserModelId = string.IsNullOrWhiteSpace(AppAumidBox.Text) ? null : AppAumidBox.Text.Trim();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "起動するものを選択", Filter = "実行ファイル・ショートカット|*.exe;*.lnk;*.bat;*.cmd;*.url|すべてのファイル|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            AppExeBox.Text = dlg.FileName;
            if (_currentApp is not null && string.IsNullOrWhiteSpace(_currentApp.Name))
                AppNameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void ChangeAppIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_currentApp is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "アイコンを選択",
            Filter = "アイコン・画像・実行ファイル|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _currentApp.IconPath = dlg.FileName;
            IconCache.Invalidate(dlg.FileName);
            UpdateAppIconPreview();
            RefreshAppRows();
        }
    }

    private void ResetAppIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_currentApp is null) return;
        _currentApp.IconPath = null;
        UpdateAppIconPreview();
        RefreshAppRows();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "追加するアプリ（実行ファイル・ショートカットなど）を選択",
            Filter = "実行ファイル・ショートカット|*.exe;*.lnk;*.bat;*.cmd;*.url|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        bool isExe = dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var app = new AppEntry
        {
            Name = GetProductName(dlg.FileName),
            ExePath = dlg.FileName,
            // exe は汎用（ジャンプリスト＋最近使った項目）から履歴を拾う。ショートカット等は直接起動
            Source = isExe ? HistorySource.Windows : HistorySource.None,
            DirectLaunch = !isExe,
        };
        _apps.Add(new AppRow(app));
        AppList.SelectedIndex = _apps.Count - 1;
        AppNameBox.Focus();
        AppNameBox.SelectAll();
    }

    private static string GetProductName(string exe)
    {
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(exe);
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row) return;
        string msg = row.App.Pins.Count > 0
            ? $"「{row.App.Name}」を削除します。ピン留め {row.App.Pins.Count} 件も一緒に消えます。よろしいですか？"
            : $"「{row.App.Name}」を削除します。よろしいですか？";
        if (MessageBox.Show(msg, AppSettings.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int idx = _apps.IndexOf(row);
        _apps.Remove(row);
        if (_apps.Count > 0) AppList.SelectedIndex = Math.Min(idx, _apps.Count - 1);
    }

    // ---- アプリ一覧のドラッグで並べ替え ----
    private const string AppRowFormat = "LunchBox.AppRow";
    private System.Windows.Point _appDragStart;
    private AppRow? _appDragRow;

    private void AppList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _appDragStart = e.GetPosition(null);
        _appDragRow = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.Content as AppRow;
    }

    private void AppList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_appDragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _appDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _appDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _appDragRow;
        _appDragRow = null;
        DragDrop.DoDragDrop(AppList, new System.Windows.DataObject(AppRowFormat, row), DragDropEffects.Move);
    }

    private void AppList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(AppRowFormat))
        {
            e.Effects = DragDropEffects.Move;
            var (item, after) = LocateDrop<ListBoxItem>(e);
            ShowInsertion(item, after);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void AppList_DragLeave(object sender, DragEventArgs e) => HideInsertion();

    private void AppList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideInsertion();
        if (!e.Data.GetDataPresent(AppRowFormat) || e.Data.GetData(AppRowFormat) is not AppRow src) return;
        var (item, after) = LocateDrop<ListBoxItem>(e);
        var dst = item?.Content as AppRow;
        if (dst is not null && ReferenceEquals(dst, src)) return;
        int from = _apps.IndexOf(src);
        if (from < 0) return;
        _apps.RemoveAt(from);
        int to = dst is null ? _apps.Count : _apps.IndexOf(dst) + (after ? 1 : 0); // 空白に落としたら末尾へ
        if (to < 0) to = _apps.Count;
        _apps.Insert(to, src);
        AppList.SelectedItem = src;
    }

    private void AppUp_Click(object sender, RoutedEventArgs e) => MoveApp(-1);
    private void AppDown_Click(object sender, RoutedEventArgs e) => MoveApp(1);

    private void MoveApp(int delta)
    {
        int i = AppList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _apps.Count) return;
        _apps.Move(i, j);
        AppList.SelectedIndex = j;
    }

    // ===================== ピン留め =====================

    private AppEntry? PinApp => (PinAppList.SelectedItem as AppRow)?.App;

    private void PinAppList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadPins();

    private void ReloadPins(PinItem? select = null)
    {
        _pins.Clear();
        var app = PinApp;
        if (app is null) return;
        foreach (var p in app.Pins) _pins.Add(new PinRow(p));
        if (select is not null)
            PinList.SelectedItem = _pins.FirstOrDefault(r => r.Pin == select);
    }

    private async Task AddPinAsync(ItemKind kind, string target, string label)
    {
        var app = PinApp;
        if (app is null)
        {
            MessageBox.Show("先に左の一覧でアプリを選んでください。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (app.Pins.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("同じショートカット先が既に登録されています。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pin = new PinItem { Kind = kind, Target = target, Label = label };
        // サーバー不達のときに画面が固まらないよう、確認はバックグラウンドで
        IsEnabled = false;
        Cursor = Cursors.Wait;
        try { await Task.Run(() => LinkResolver.CaptureIdentity(pin)); }
        finally { Cursor = null; IsEnabled = true; }
        app.Pins.Add(pin);
        ReloadPins(pin);
    }

    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (PinApp is null) { await AddPinAsync(ItemKind.File, "", ""); return; }
        var dlg = new OpenFileDialog { Title = "ピン留めするファイルを選択", Multiselect = true, CheckFileExists = false };
        if (dlg.ShowDialog(this) != true) return;
        foreach (string f in dlg.FileNames)
            await AddPinAsync(Directory.Exists(f) ? ItemKind.Folder : ItemKind.File, f, "");
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PinApp is null) { await AddPinAsync(ItemKind.Folder, "", ""); return; }
        var dlg = new OpenFolderDialog { Title = "ピン留めするフォルダを選択", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (string f in dlg.FolderNames)
            await AddPinAsync(ItemKind.Folder, f, "");
    }

    private async void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        var app = PinApp;
        if (app is null) { await AddPinAsync(ItemKind.Url, "", ""); return; }
        var pin = new PinItem { Kind = ItemKind.Url, Target = "https://" };
        var dlg = new EditItemWindow(pin, app.Name) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            app.Pins.Add(dlg.Result);
            ReloadPins(dlg.Result);
        }
    }

    private void EditPin_Click(object sender, RoutedEventArgs e) => EditSelectedPin();

    private void PinList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PinList.SelectedItem is PinRow) EditSelectedPin();
    }

    private void EditSelectedPin()
    {
        var app = PinApp;
        if (app is null || PinList.SelectedItem is not PinRow row) return;
        var dlg = new EditItemWindow(row.Pin.Clone(), app.Name) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            int idx = app.Pins.IndexOf(row.Pin);
            if (idx >= 0) app.Pins[idx] = dlg.Result;
            ReloadPins(dlg.Result);
        }
    }

    private void RemovePin_Click(object sender, RoutedEventArgs e)
    {
        var app = PinApp;
        if (app is null) return;
        var rows = PinList.SelectedItems.Cast<PinRow>().ToList();
        if (rows.Count == 0) return;
        string msg = rows.Count == 1 ? $"「{rows[0].Label}」を削除します。よろしいですか？" : $"{rows.Count} 件を削除します。よろしいですか？";
        if (MessageBox.Show(msg, AppSettings.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var r in rows) app.Pins.Remove(r.Pin);
        ReloadPins();
    }

    private void PinUp_Click(object sender, RoutedEventArgs e) => MovePin(-1);
    private void PinDown_Click(object sender, RoutedEventArgs e) => MovePin(1);

    private void MovePin(int delta)
    {
        var app = PinApp;
        if (app is null || PinList.SelectedItem is not PinRow row) return;
        int i = app.Pins.IndexOf(row.Pin);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= app.Pins.Count) return;
        (app.Pins[i], app.Pins[j]) = (app.Pins[j], app.Pins[i]);
        ReloadPins(row.Pin);
    }

    private void CheckAllPins_Click(object sender, RoutedEventArgs e)
    {
        var app = PinApp;
        if (app is null) return;
        Cursor = Cursors.Wait;
        try
        {
            foreach (var p in app.Pins) LinkResolver.Check(p);
        }
        finally
        {
            Cursor = null;
        }
        ReloadPins();
        int nf = app.Pins.Count(p => p.Status == LinkStatus.NotFound);
        MessageBox.Show(nf == 0 ? "すべて見つかりました。" : $"{nf} 件が見つかりません。編集で場所を指定し直してください。",
            AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- 挿入線（ドラッグ中に、落とすと入る位置へ線を出す） ----
    private sealed class InsertionAdorner : System.Windows.Documents.Adorner
    {
        private readonly bool _after;
        public InsertionAdorner(UIElement item, bool after) : base(item) { _after = after; IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext dc)
        {
            double w = AdornedElement.RenderSize.Width, h = AdornedElement.RenderSize.Height;
            double y = _after ? h - 1 : 1;
            var brush = System.Windows.SystemColors.HighlightBrush;
            dc.DrawLine(new System.Windows.Media.Pen(brush, 2), new System.Windows.Point(4, y), new System.Windows.Point(w - 4, y));
            dc.DrawEllipse(brush, null, new System.Windows.Point(4, y), 3, 3);
            dc.DrawEllipse(brush, null, new System.Windows.Point(w - 4, y), 3, 3);
        }
    }

    private InsertionAdorner? _insertion;

    private void ShowInsertion(UIElement? item, bool after)
    {
        HideInsertion();
        if (item is null) return;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(item);
        if (layer is null) return;
        _insertion = new InsertionAdorner(item, after);
        layer.Add(_insertion);
    }

    private void HideInsertion()
    {
        if (_insertion is null) return;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(_insertion.AdornedElement);
        layer?.Remove(_insertion);
        _insertion = null;
    }

    /// <summary>ドロップ先の行と「その下に入れるか」を求める（行が無ければ末尾）</summary>
    private static (T? item, bool after) LocateDrop<T>(DragEventArgs e) where T : FrameworkElement
    {
        var item = FindAncestor<T>(e.OriginalSource as DependencyObject);
        if (item is null) return (null, true);
        bool after = e.GetPosition(item).Y > item.ActualHeight / 2;
        return (item, after);
    }

    // ---- 一覧内のドラッグで並べ替え ----
    private const string PinRowFormat = "LunchBox.PinRow";
    private System.Windows.Point _dragStart;
    private PinRow? _dragRow;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void PinList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragRow = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.Content as PinRow;
    }

    private void PinList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        var data = new System.Windows.DataObject(PinRowFormat, row);
        DragDrop.DoDragDrop(PinList, data, DragDropEffects.Move);
    }

    private void PinList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(PinRowFormat))
        {
            e.Effects = DragDropEffects.Move;
            var (item, after) = LocateDrop<ListViewItem>(e);
            ShowInsertion(item, after);
        }
        else
        {
            e.Effects = PinApp is not null && DropParser.CanAccept(e.Data) ? DragDropEffects.Link : DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void PinList_DragLeave(object sender, DragEventArgs e) => HideInsertion();

    private async void PinList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (PinApp is null) return;

        // 一覧内の並べ替え
        if (e.Data.GetDataPresent(PinRowFormat) && e.Data.GetData(PinRowFormat) is PinRow src)
        {
            HideInsertion();
            var app = PinApp;
            var (item, after) = LocateDrop<ListViewItem>(e);
            var dstRow = item?.Content as PinRow;
            if (dstRow is not null && ReferenceEquals(dstRow.Pin, src.Pin)) return;
            if (!app.Pins.Remove(src.Pin)) return;
            int to = dstRow is null ? app.Pins.Count : app.Pins.IndexOf(dstRow.Pin) + (after ? 1 : 0); // 空白に落としたら末尾へ
            if (to < 0) to = app.Pins.Count;
            app.Pins.Insert(to, src.Pin);
            ReloadPins(src.Pin);
            return;
        }

        var items = DropParser.Parse(e.Data);
        if (items.Count == 0)
        {
            MessageBox.Show("登録できる項目が見つかりませんでした。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var d in items)
            await AddPinAsync(d.Kind, d.Target, d.Label);
    }

    // ===================== 全般 =====================

    private void SelectLinkCheck(int minutes)
    {
        foreach (ComboBoxItem it in LinkCheckBox.Items)
        {
            if (it.Tag is string t && int.TryParse(t, out int m) && m == minutes)
            {
                LinkCheckBox.SelectedItem = it;
                return;
            }
        }
        LinkCheckBox.SelectedIndex = 0;
    }

    private void HotKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotKeyBox.Text = "キーを押してください...";
    }

    private void HotKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
    }

    private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (HotKeyText.IsModifierKey(key))
        {
            HotKeyBox.Text = HotKeyText.WithSeparator(
                HotKeyText.FormatModifiers(HotKeyText.FromWpfModifiers(Keyboard.Modifiers))) + "...";
            return;
        }
        if (key == Key.Escape)
        {
            HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
            return;
        }

        var (mods, vk) = HotKeyText.FromWpf(key, Keyboard.Modifiers);
        if (vk == 0) return;
        _pendingModifiers = mods;
        _pendingVk = vk;
        HotKeyBox.Text = HotKeyText.Format(mods, vk);
    }

    // ---- 通知の表示先 ----
    private sealed record NotifyMonitorItem(string Key, string Label);

    private void NotifyModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NotifyMonitorBox is not null) NotifyMonitorBox.IsEnabled = NotifyModeBox.SelectedIndex == 0;
    }

    // ---- 年度カレンダー ----
    private (uint Mods, uint Vk) _calKey;

    private void CalKeyBox_GotFocus(object sender, RoutedEventArgs e) => CalKeyBox.Text = "キーを押してください...";

    private void CalKeyBox_LostFocus(object sender, RoutedEventArgs e) => CalKeyBox.Text = LayoutKeyText(_calKey);

    private void CalKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (HotKeyText.IsModifierKey(key))
        {
            CalKeyBox.Text = HotKeyText.WithSeparator(HotKeyText.FormatModifiers(HotKeyText.FromWpfModifiers(Keyboard.Modifiers))) + "...";
            return;
        }
        if (key == Key.Escape) { CalKeyBox.Text = LayoutKeyText(_calKey); return; }
        var (mods, vk) = HotKeyText.FromWpf(key, Keyboard.Modifiers);
        if (vk == 0) return;
        if (mods == 0) { CalKeyBox.Text = "Ctrl・Alt・Shift・Win と組み合わせてください"; return; }
        _calKey = (mods, vk);
        CalKeyBox.Text = HotKeyText.Format(mods, vk);
    }

    private void CalKeyReset_Click(object sender, RoutedEventArgs e)
    {
        _calKey = (HotKeyService.MOD_ALT | HotKeyService.MOD_SHIFT, 0x5A);
        CalKeyBox.Text = LayoutKeyText(_calKey);
    }

    private void CalKeyClear_Click(object sender, RoutedEventArgs e)
    {
        _calKey = (0, 0);
        CalKeyBox.Text = LayoutKeyText(_calKey);
    }

    private void RefreshCalendarStatus()
    {
        var h = (System.Windows.Application.Current as App)?.Holidays;
        CalHolidayStatusText.Text = h is null ? "祝日データを読み込めていません。" : h.StatusText();
        CalUpdateNowButton.IsEnabled = h is not null;
    }

    private async void CalUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        var h = (System.Windows.Application.Current as App)?.Holidays;
        if (h is null) return;
        CalUpdateNowButton.IsEnabled = false;
        CalUpdateNowButton.Content = "更新中...";
        try { await h.UpdateAsync(DateTime.Now); }
        catch (Exception ex) { Logger.Log("祝日データの手動更新に失敗: " + ex.Message); }
        CalUpdateNowButton.Content = "今すぐ更新";
        RefreshCalendarStatus();
    }

    private void CalListBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshCalendarCounts();

    private void RefreshCalendarCounts()
    {
        if (CalCompanyCountText is null || CalAddsCountText is null || CalRemovesCountText is null) return;
        static string Count(string text)
        {
            var (items, bad) = HolidayText.Parse(text);
            return $"読み取れた日数：{items.Count} 日" + (bad > 0 ? $"　／　読み取れない行：{bad} 行（日付は 2026/12/29 の形で）" : "");
        }
        CalCompanyCountText.Text = Count(CalCompanyBox.Text);
        CalAddsCountText.Text = Count(CalAddsBox.Text);
        CalRemovesCountText.Text = Count(CalRemovesBox.Text);
    }

    // ---- ウインドウ配置のホットキー ----
    private (uint Mods, uint Vk) _layRestoreKey;
    private (uint Mods, uint Vk) _layRecallKey;

    private static string LayoutKeyText((uint Mods, uint Vk) k) => k.Vk == 0 ? "（割り当てなし）" : HotKeyText.Format(k.Mods, k.Vk);

    private (uint Mods, uint Vk) LayoutKeyOf(object sender) => ReferenceEquals(sender, LayoutRestoreKeyBox) ? _layRestoreKey : _layRecallKey;

    private void LayoutKeyBox_GotFocus(object sender, RoutedEventArgs e) => ((System.Windows.Controls.TextBox)sender).Text = "キーを押してください...";

    private void LayoutKeyBox_LostFocus(object sender, RoutedEventArgs e) => ((System.Windows.Controls.TextBox)sender).Text = LayoutKeyText(LayoutKeyOf(sender));

    private void LayoutKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (System.Windows.Controls.TextBox)sender;
        Key key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (HotKeyText.IsModifierKey(key))
        {
            box.Text = HotKeyText.WithSeparator(HotKeyText.FormatModifiers(HotKeyText.FromWpfModifiers(Keyboard.Modifiers))) + "...";
            return;
        }
        if (key == Key.Escape) { box.Text = LayoutKeyText(LayoutKeyOf(sender)); return; }
        var (mods, vk) = HotKeyText.FromWpf(key, Keyboard.Modifiers);
        if (vk == 0) return;
        if (mods == 0) { box.Text = "Ctrl・Alt・Shift・Win と組み合わせてください"; return; }
        if (ReferenceEquals(sender, LayoutRestoreKeyBox)) _layRestoreKey = (mods, vk); else _layRecallKey = (mods, vk);
        box.Text = HotKeyText.Format(mods, vk);
    }

    private void LayoutRestoreKeyClear_Click(object sender, RoutedEventArgs e)
    {
        _layRestoreKey = (0, 0);
        LayoutRestoreKeyBox.Text = LayoutKeyText(_layRestoreKey);
    }

    private void LayoutRecallKeyClear_Click(object sender, RoutedEventArgs e)
    {
        _layRecallKey = (0, 0);
        LayoutRecallKeyBox.Text = LayoutKeyText(_layRecallKey);
    }

    private void ResetHotKey_Click(object sender, RoutedEventArgs e)
    {
        _pendingModifiers = HotKeyService.MOD_CONTROL;
        _pendingVk = 0x1D;
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
    }

    // ===================== 保存 =====================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _apps)
        {
            if (string.IsNullOrWhiteSpace(r.App.Name))
            {
                MessageBox.Show("表示名が空のアプリがあります。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
                Tabs.SelectedIndex = 0;
                AppList.SelectedItem = r;
                return;
            }
        }

        _settings.Apps = _apps.Select(r => r.App).ToList();
        _settings.HotKeyEnabled = HotKeyEnabledBox.IsChecked == true;
        _settings.HotKeyOpensSearch = HotKeyTargetBox.SelectedIndex != 1;
        _settings.SwapTrayClicks = TrayClickBox.SelectedIndex == 1;
        _settings.HotKeyModifiers = _pendingModifiers;
        _settings.HotKeyVirtualKey = _pendingVk;
        _settings.NotifyPlaceEnabled = NotifyEnabledBox.IsChecked == true;
        _settings.NotifyPlaceMode = Math.Max(0, NotifyModeBox.SelectedIndex);
        _settings.NotifyPlaceMonitor = (NotifyMonitorBox.SelectedItem as NotifyMonitorItem)?.Key ?? "";
        _settings.CalendarEnabled = CalEnabledBox.IsChecked == true;
        (_settings.CalendarHotKeyModifiers, _settings.CalendarHotKeyVirtualKey) = _calKey;
        _settings.CalendarMonitor = Math.Max(0, CalMonitorBox.SelectedIndex);
        _settings.CalendarWeekStartsMonday = CalWeekStartBox.SelectedIndex == 1;
        _settings.CalendarHolidayUpdateOnStartup = CalUpdateOnStartupBox.IsChecked == true;
        _settings.CalendarHolidayAutoUpdate = CalAutoUpdateBox.IsChecked == true;
        _settings.CalendarShowProvisional = CalProvisionalBox.SelectedIndex != 1;
        _settings.CalendarCompanyHolidays = CalCompanyBox.Text;
        _settings.CalendarHolidayAdds = CalAddsBox.Text;
        _settings.CalendarHolidayRemoves = CalRemovesBox.Text;
        _settings.LayoutAutoRecord = LayoutAutoRecordBox.IsChecked == true;
        _settings.LayoutAutoRestoreMode = Math.Max(0, LayoutAutoRestoreBox.SelectedIndex);
        _settings.LayoutLaunchMissing = LayoutLaunchMissingBox.IsChecked == true;
        (_settings.LayoutRestoreHotKeyModifiers, _settings.LayoutRestoreHotKeyVirtualKey) = _layRestoreKey;
        (_settings.LayoutRecallHotKeyModifiers, _settings.LayoutRecallHotKeyVirtualKey) = _layRecallKey;
        if (int.TryParse(RecentCountBox.Text, out int rc)) _settings.RecentCount = rc;
        if (int.TryParse(HistoryMaxBox.Text, out int hm)) _settings.HistoryMaxPerApp = hm;
        if (int.TryParse(MaxLabelBox.Text, out int ml)) _settings.MaxLabelLength = ml;
        _settings.ChromeProfile = string.IsNullOrWhiteSpace(ChromeProfileBox.Text) ? "Default" : ChromeProfileBox.Text.Trim();
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.RunElevated = RunElevatedBox.IsChecked == true;
        _settings.SwitcherEnabled = SwitcherEnabledBox.IsChecked == true;
        if (LinkCheckBox.SelectedItem is ComboBoxItem it && it.Tag is string t && int.TryParse(t, out int m))
            _settings.LinkCheckIntervalMinutes = m;
        _settings.ClipboardEnabled = ClipEnabledBox.IsChecked == true;
        _settings.ClipboardKeepImages = ClipImagesBox.IsChecked == true;
        if (int.TryParse(ClipMaxTextBox.Text, out int cmt)) _settings.ClipboardMaxText = cmt;
        if (int.TryParse(ClipMaxImagesBox.Text, out int cmi)) _settings.ClipboardMaxImages = cmi;
        if (int.TryParse(ClipMenuCountBox.Text, out int cmc)) _settings.ClipboardMenuCount = cmc;
        _settings.ClipboardPersist = ClipPersistBox.IsChecked == true;
        _settings.ClipboardArchiveOverflow = ClipArchiveBox.IsChecked == true;
        _settings.ClipboardArchiveFolder = ClipArchiveFolderBox.Text.Trim().Trim('"');
        _settings.Sanitize();

        SettingsSaved?.Invoke(_settings);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
