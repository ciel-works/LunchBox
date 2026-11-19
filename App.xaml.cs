using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace LunchBox;

public partial class App : Application
{
    private WinForms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    private TrayMenu? _menu;
    private HotKeyService? _hotKeys;
    private SettingsWindow? _settingsWindow;
    private Mutex? _singleInstanceMutex;
    private DispatcherTimer? _linkCheckTimer;
    private DispatcherTimer? _collectTimer;
    private HistoryCollector? _collector;
    private ExplorerWatcher? _explorerWatcher;
    private ClipboardMonitor? _clipboard;
    private SearchWindow? _search;
    // ウインドウ切替（旧 FrontSwitcher）
    private WindowSwitcher? _switcher;
    private MouseGestureService? _gestures;
    private HotKeyService? _switchHotKey;
    private SwitcherSettingsWindow? _switcherWindow;
    private bool _gesturePaused;
    private bool _ownsMutex;
    private bool _migratedNow;
    private readonly object _clipsSaveGate = new();
    public ClipStore Clips { get; private set; } = new();
    private readonly object _saveGate = new();
    private readonly object _historySaveGate = new();

    public AppSettings Settings { get; private set; } = new();
    public HistoryStore History { get; private set; } = new();

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // 画面はソフトウェア描画にする（11/16）。ハードウェア描画は、最初にウインドウを出した時点でグラフィックス用のメモリを
        // 数十〜百 MB 確保し、ウインドウを閉じても返さない。常駐アプリで画面は単純なので、ソフトウェア描画で十分
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 想定外の例外で落ちないように／落ちても原因がログに残るように
        DispatcherUnhandledException += (_, ev) =>
        {
            Logger.Log("想定外の例外(UI): " + ev.Exception);
            ev.Handled = true;
        };
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ev) => Logger.Log("想定外の例外(メニュー): " + ev.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Logger.Log("想定外の例外(致命的): " + ev.ExceptionObject);
            // 落ちる前に、通知ウインドウをメインモニタの元の位置へ戻す（置き去りにしない）
            try { if (Settings.NotifyPlaceEnabled) _notifyPlacer?.RestoreOriginal("不正終了"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => { Logger.Log("想定外の例外(タスク): " + ev.Exception); ev.SetObserved(); };

        bool firstRun = !System.IO.File.Exists(AppSettings.SettingsPath);
        Settings = AppSettings.Load();

        // 「管理者権限で常駐」が ON なのに一般権限で起動された：昇格して起動し直す
        if (Settings.RunElevated && !Elevation.IsElevated && TryRelaunchElevated())
        {
            Shutdown();
            return;
        }

        // 二重起動防止（設定変更による起動し直しでは前のプロセスの終了を少し待つ）
        _singleInstanceMutex = new Mutex(false, "LunchBox_SingleInstance_Mutex");
        try { _ownsMutex = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(6)); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex)
        {
            MessageBox.Show("LunchBox は既に起動しています。", AppSettings.AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        Logger.Log($"=== LunchBox 起動 v{Assembly.GetExecutingAssembly().GetName().Version} 管理者={Elevation.IsElevated} ===");
        if (Elevation.IsElevated) AllowTaskbarCreatedMessage();
        History = HistoryStore.Load();
        MigrateFrontSwitcher();

        if (firstRun)
        {
            Settings.Apps = AppDetector.Detect();
            Logger.Log("初回起動: アプリ自動検出 " + string.Join(", ", Settings.Apps.Select(a => a.Name)));
            Settings.Save();
        }

        StartupManager.Apply(Settings.StartWithWindows, Settings.RunElevated);

        // 統合後は FrontSwitcher を使わないので、その自動起動タスクを片付ける（管理者で動いているときに 1 回だけ）
        if (Settings.SwitcherEnabled && !Settings.FrontSwitcherTaskRemoved && Elevation.IsElevated)
        {
            if (StartupManager.RemoveFrontSwitcherTask())
            {
                Settings.FrontSwitcherTaskRemoved = true;
                Settings.Save();
            }
        }

        InitTrayIcon();
        InitHotKey();
        InitSwitcher();
        InitLayouts();
        InitCalendar();
        InitNotifyPlacer();
        StartLinkCheck();
        InitHistoryCollection();
        InitClipboard();

        if (firstRun)
        {
            ShowBalloon("LunchBox を常駐しました。トレイアイコンをクリックするとメニューが開きます。");
            OpenSettings(null);
        }

        // FrontSwitcher から引き継いだ直後：管理者権限に切り替えて起動し直す（UAC の確認が 1 回出る）
        if (_migratedNow && Settings.RunElevated && !Elevation.IsElevated)
        {
            ShowBalloon("FrontSwitcher の設定を引き継ぎました。管理者権限で起動し直します。");
            Dispatcher.BeginInvoke(new Action(RestartForElevationChange), DispatcherPriority.ApplicationIdle);
        }
    }

    // ---------- 管理者権限での常駐 ----------

    /// <summary>昇格して起動し直す。タスクがあれば UAC なし、無ければ UAC を出す。起動できたら true</summary>
    private static bool TryRelaunchElevated()
    {
        try
        {
            if (StartupManager.TaskExists() && StartupManager.RunTask())
            {
                Logger.Log("昇格用タスクで起動し直します");
                return true;
            }
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return false;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Logger.Log("UAC で昇格して起動し直します");
            return true;
        }
        catch (Exception ex)
        {
            // UAC のキャンセルなど：一般権限のまま続ける
            Logger.Log("昇格できませんでした（一般権限で続行）: " + ex.Message);
            return false;
        }
    }

    /// <summary>昇格プロセスでも Explorer の "TaskbarCreated" を受け取れるようにする（トレイアイコンが消えない対策）</summary>
    private static void AllowTaskbarCreatedMessage()
    {
        try
        {
            uint msg = SwNative.RegisterWindowMessage("TaskbarCreated");
            if (msg != 0) SwNative.ChangeWindowMessageFilter(msg, SwNative.MSGFLT_ADD);
        }
        catch (Exception ex)
        {
            Logger.Log("ChangeWindowMessageFilter 失敗: " + ex.Message);
        }
    }

    /// <summary>設定の「管理者権限で常駐」と実際の権限が食い違っていたら、合わせて起動し直す</summary>
    private void RestartForElevationChange()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return;
        try
        {
            if (Settings.RunElevated && !Elevation.IsElevated)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                Shutdown();
            }
            else if (!Settings.RunElevated && Elevation.IsElevated)
            {
                // エクスプローラー経由なら一般権限で起動する
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = true });
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("権限の切り替えに失敗: " + ex.Message);
            if (Settings.RunElevated && !Elevation.IsElevated)
            {
                Settings.RunElevated = false;
                SaveSettings();
                ShowBalloon("管理者権限への切り替えがキャンセルされました。一般権限のまま動作します。");
            }
        }
    }

    // ---------- ウインドウ切替（旧 FrontSwitcher） ----------

    /// <summary>初回だけ、FrontSwitcher の設定（%AppData%\FrontSwitcher\settings.json）を引き継ぐ</summary>
    private void MigrateFrontSwitcher()
    {
        if (Settings.SwitcherMigrated) return;
        Settings.SwitcherMigrated = true;
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrontSwitcher", "settings.json");
            if (System.IO.File.Exists(path))
            {
                var old = System.Text.Json.JsonSerializer.Deserialize<SwitcherSettings>(System.IO.File.ReadAllText(path));
                if (old is not null)
                {
                    old.Normalize();
                    Settings.Switcher = old;
                    Settings.SwitcherEnabled = true;
                    // FrontSwitcher は常に管理者権限で動いていた。同じ動き（昇格アプリの窓も隠せる）を保つ
                    Settings.RunElevated = true;
                    _migratedNow = true;
                    Logger.Log($"FrontSwitcher の設定を引き継ぎました（一緒に最小化 {old.MinimizeWithProcesses.Count} 件・ジェスチャ {old.MouseGestures.Count} 件）");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("FrontSwitcher の設定の引き継ぎ失敗: " + ex.Message);
        }
        Settings.Save();
    }

    // ---------- 通知バナーの表示先 ----------

    private NotificationPlacer? _notifyPlacer;

    /// <summary>設定画面に出す：通知の表示先の状態</summary>
    public string NotifyPlaceStatus => _notifyPlacer?.Status ?? "";

    private void InitNotifyPlacer()
    {
        try
        {
            _notifyPlacer = new NotificationPlacer();
            ApplyNotifyPlacer();
        }
        catch (Exception ex)
        {
            Logger.Log("通知の表示先の初期化に失敗: " + ex);
        }
        if (_menu is not null)
        {
            _menu.NotifyPlaceToggled += on =>
            {
                Settings.NotifyPlaceEnabled = on;
                SaveSettings();
                ApplyNotifyPlacer();
                ShowBalloon(on ? "通知の表示先：" + NotifyPlaceStatus : "通知は Windows の位置（メインモニタ）に戻しました。");
            };
        }
    }

    private void ApplyNotifyPlacer()
    {
        try { _notifyPlacer?.Apply(Settings.NotifyPlaceEnabled, Settings.NotifyPlaceMode, Settings.NotifyPlaceMonitor); }
        catch (Exception ex) { Logger.Log("通知の表示先の設定に失敗: " + ex.Message); }
    }

    // ---------- 年度カレンダー ----------

    private HolidayService? _holidays;
    private CalendarWindow? _calendarWindow;
    private HotKeyService? _calendarHotKey;
    private DispatcherTimer? _holidayTimer;

    /// <summary>設定画面から使う：祝日データ</summary>
    public HolidayService? Holidays => _holidays;
    /// <summary>設定画面に出す：ホットキーの登録結果</summary>
    public string CalendarHotKeyStatus { get; private set; } = "";

    private void InitCalendar()
    {
        try
        {
            _holidays = new HolidayService(AppSettings.HolidayDirectory);
            Logger.Log($"年度カレンダー: 祝日データ {_holidays.Rows} 件（{_holidays.Origin}・{_holidays.LastHoliday:yyyy年M月}まで）");
            var diffs = _holidays.CrossCheck();
            if (diffs.Count > 0)
                Logger.Log($"年度カレンダー: 計算値と公表データの食い違い {diffs.Count} 件（公表データを優先）: " + string.Join(" / ", diffs.Take(10)));
        }
        catch (Exception ex)
        {
            Logger.Log("年度カレンダーの初期化に失敗: " + ex);
            return;
        }
        _calendarHotKey = new HotKeyService(0xB006);
        _calendarHotKey.HotKeyPressed += ToggleCalendar;
        if (_menu is not null)
        {
            _menu.CalendarRequested += () =>
            {
                // メニューが閉じきってから出す
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                t.Tick += (_, _) => { t.Stop(); ToggleCalendar(); };
                t.Start();
            };
        }
        ApplyCalendarSettings();

        // 祝日データの更新：起動 40 秒後に 1 回（表示の操作の中では通信しない）、以後は 1 時間ごとに「月が変わったか」を見る
        bool first = true;
        _holidayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        _holidayTimer.Tick += async (_, _) =>
        {
            _holidayTimer.Interval = TimeSpan.FromHours(1);
            bool startup = first;
            first = false;
            try
            {
                if (_holidays is null || !Settings.CalendarEnabled) return;
                var now = DateTime.Now;
                bool run = (startup && Settings.CalendarHolidayUpdateOnStartup)
                           || (Settings.CalendarHolidayAutoUpdate && _holidays.ShouldUpdate(now));
                if (run) await _holidays.UpdateAsync(now);
            }
            catch (Exception ex) { Logger.Log("祝日データの更新に失敗: " + ex.Message); }
        };
        _holidayTimer.Start();
    }

    private void ApplyCalendarSettings()
    {
        if (_calendarHotKey is null) return;
        _calendarHotKey.Unregister();
        if (!Settings.CalendarEnabled)
        {
            CalendarHotKeyStatus = "";
            _calendarWindow?.Hide();
            return;
        }
        if (Settings.CalendarHotKeyVirtualKey == 0) { CalendarHotKeyStatus = "ホットキーは割り当てなし（メニューから開けます）"; return; }
        string key = HotKeyText.Format(Settings.CalendarHotKeyModifiers, Settings.CalendarHotKeyVirtualKey);
        if (_calendarHotKey.Register(Settings.CalendarHotKeyModifiers, Settings.CalendarHotKeyVirtualKey))
        {
            CalendarHotKeyStatus = $"{key} を登録済み";
        }
        else
        {
            CalendarHotKeyStatus = $"{key} を登録できませんでした（他のアプリか、LunchBox の別のホットキーが使用中）";
            Logger.Log("年度カレンダー: ホットキー " + CalendarHotKeyStatus);
        }
    }

    private void ToggleCalendar()
    {
        try
        {
            if (_calendarWindow is { IsVisible: true }) { _calendarWindow.Hide(); return; }
            if (!Settings.CalendarEnabled || _holidays is null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool created = _calendarWindow is null;
            _calendarWindow ??= new CalendarWindow();
            var screens = WinForms.Screen.AllScreens.Select(s => (s.Bounds, s.WorkingArea, s.Primary)).ToList();
            var area = CalendarWindow.PickArea(Settings.CalendarMonitor, screens, WinForms.Cursor.Position);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var s = Settings;
            var holidays = _holidays;
            string signature = string.Join("|", today, s.CalendarWeekStartsMonday, s.CalendarShowProvisional, holidays.Rows, holidays.State.LastSuccessAt?.Ticks,
                                           s.CalendarCompanyHolidays, s.CalendarHolidayAdds, s.CalendarHolidayRemoves);
            _calendarWindow.ShowOn(area, signature, () => new CalendarData(holidays, s, today));
            if (created || sw.ElapsedMilliseconds > 200) Logger.Log($"年度カレンダー: 表示 {sw.ElapsedMilliseconds}ms（{area.Width}×{area.Height} @{area.X},{area.Y}）");
        }
        catch (Exception ex)
        {
            Logger.Log("年度カレンダーの表示に失敗: " + ex);
        }
    }

    // ---------- ウインドウ配置の記憶 ----------

    private WindowLayoutService? _layouts;

    private void InitLayouts()
    {
        try
        {
            _layouts = new WindowLayoutService(AppSettings.LayoutsPath)
            {
                AutoRecord = Settings.LayoutAutoRecord, AutoRestoreMode = Settings.LayoutAutoRestoreMode,
            };
            _layouts.Notify += ShowBalloon;
            _layoutRestoreHotKey = new HotKeyService(0xB004);
            _layoutRestoreHotKey.HotKeyPressed += () =>
            {
                var prev = _layouts.Current.Previous;
                if (prev.Count == 0) { ShowBalloon("この画面構成には、直前の配置の記録がありません。"); return; }
                ShowBalloon(_layouts.Restore(prev[0], Settings.LayoutLaunchMissing).Message);
            };
            _layoutRecallHotKey = new HotKeyService(0xB005);
            _layoutRecallHotKey.HotKeyPressed += () =>
            {
                var saves = _layouts.Current.Saves;
                if (saves.Count == 0) { ShowBalloon("この画面構成には、セーブがありません。"); return; }
                ShowBalloon($"「{saves[0].Name}」：" + _layouts.Restore(saves[0], Settings.LayoutLaunchMissing).Message);
            };
            ApplyLayoutSettings();
        }
        catch (Exception ex)
        {
            Logger.Log("ウインドウ配置の初期化に失敗: " + ex);
            return;
        }
        if (_menu is null) return;
        _menu.Layouts = _layouts;
        // メニューが閉じきってから動かす（閉じる途中だと前面のウインドウが入れ替わる）
        void Later(Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (_, _) => { t.Stop(); try { a(); } catch (Exception ex) { Logger.Log("ウインドウ配置の操作に失敗: " + ex); } };
            t.Start();
        }
        _menu.LayoutSaveRequested += () => Later(() =>
        {
            var cfg = _layouts.Current;
            string? name = TextPrompt.Show("いまの配置をセーブ", $"セーブの名前（{cfg.Description}）", $"配置{cfg.Saves.Count + 1}");
            if (name is null) return;
            var snap = _layouts.Save(name);
            ShowBalloon($"「{name}」をセーブしました（{snap.Windows.Count} ウインドウ）");
        });
        _menu.LayoutRecallRequested += snap => Later(() => ShowBalloon(_layouts.Restore(snap, Settings.LayoutLaunchMissing).Message));
        _menu.OpenLayoutSettingsRequested += () => OpenSettings(null, "ウインドウ配置");
        _menu.LayoutOverwriteRequested += snap => Later(() =>
        {
            _layouts.Overwrite(snap);
            ShowBalloon($"「{snap.Name}」をいまの配置で上書きしました（{snap.Windows.Count} ウインドウ）");
        });
        _menu.LayoutRenameRequested += snap => Later(() =>
        {
            string? name = TextPrompt.Show("セーブの名前を変更", "新しい名前", snap.Name);
            if (name is not null) _layouts.Rename(snap, name);
        });
        _menu.LayoutDeleteRequested += snap => Later(() =>
        {
            if (MessageBox.Show($"セーブ「{snap.Name}」を削除しますか？", AppSettings.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _layouts.Delete(snap);
        });
    }

    private HotKeyService? _layoutRestoreHotKey;
    private HotKeyService? _layoutRecallHotKey;

    private void ApplyLayoutSettings()
    {
        if (_layouts is null) return;
        _layouts.AutoRecord = Settings.LayoutAutoRecord;
        _layouts.AutoRestoreMode = Settings.LayoutAutoRestoreMode;
        _layoutRestoreHotKey?.Unregister();
        _layoutRecallHotKey?.Unregister();
        if (Settings.LayoutRestoreHotKeyVirtualKey != 0
            && _layoutRestoreHotKey?.Register(Settings.LayoutRestoreHotKeyModifiers, Settings.LayoutRestoreHotKeyVirtualKey) == false)
            ShowBalloon("「直前の配置に戻す」のホットキーを登録できませんでした（他のアプリが使用中）。");
        if (Settings.LayoutRecallHotKeyVirtualKey != 0
            && _layoutRecallHotKey?.Register(Settings.LayoutRecallHotKeyModifiers, Settings.LayoutRecallHotKeyVirtualKey) == false)
            ShowBalloon("「先頭のセーブを呼び出す」のホットキーを登録できませんでした（他のアプリが使用中）。");
    }

    private void InitSwitcher()
    {
        _switcher = new WindowSwitcher();
        WindowSwitcher.TrayBalloonRequested += ShowBalloon;

        _switchHotKey = new HotKeyService(0xB003);
        _switchHotKey.HotKeyPressed += async () => await DoSwitchAsync();

        _gestures = new MouseGestureService();
        // フックスレッドから呼ばれるので UI スレッドへ回す
        _gestures.GestureRecognized += (binding, target) =>
            Dispatcher.BeginInvoke(new Action(async () => await DoGestureAsync(binding.Action, target)));

        if (_menu is not null)
        {
            _menu.SwitcherState = () => (Settings.SwitcherEnabled, Settings.SwitcherEnabled && Settings.Switcher.MouseGestureEnabled, _gesturePaused);
            _menu.SwitchNowRequested += async () => await DoSwitchAsync();
            _menu.RestoreHiddenRequested += RestoreHidden;
            _menu.GesturePauseChanged += paused =>
            {
                _gesturePaused = paused;
                if (_gestures is not null) _gestures.Paused = paused;
                Logger.Log($"MouseGesture: 一時停止={paused}");
            };
            _menu.OpenSwitcherSettingsRequested += OpenSwitcherSettings;
        }
        ApplySwitcher();
    }

    /// <summary>現在の設定で、切替のホットキーとマウスジェスチャを開始／停止する</summary>
    private void ApplySwitcher()
    {
        if (_switchHotKey is null || _gestures is null) return;
        if (Settings.SwitcherEnabled)
        {
            var sw = Settings.Switcher;
            // 年度カレンダーと同じキーなら、カレンダーを優先して切替のホットキーは外す
            // （切替はマウスジェスチャで使う想定なので、ホットキーは外してよい）
            if (Settings.CalendarEnabled && sw.VirtualKey != 0
                && sw.VirtualKey == Settings.CalendarHotKeyVirtualKey && sw.Modifiers == Settings.CalendarHotKeyModifiers)
            {
                Logger.Log($"切替のホットキー [{HotKeyText.Format(sw.Modifiers, sw.VirtualKey)}] は年度カレンダーと同じなので外しました（ジェスチャはそのまま）");
                sw.VirtualKey = 0;
                SaveSettings();
            }
            if (sw.VirtualKey == 0)
                _switchHotKey.Unregister();
            else if (!_switchHotKey.Register(sw.Modifiers, sw.VirtualKey))
                ShowBalloon($"切替のホットキー [{HotKeyText.Format(sw.Modifiers, sw.VirtualKey)}] を登録できませんでした。" +
                            "FrontSwitcher がまだ動いている場合は終了してください。");
            _gestures.Apply(sw);
        }
        else
        {
            _switchHotKey.Unregister();
            var off = Settings.Switcher.Clone();
            off.MouseGestureEnabled = false;
            _gestures.Apply(off);
        }
    }

    private async Task DoSwitchAsync()
    {
        if (_switcher is null || !Settings.SwitcherEnabled) return;
        try { await _switcher.SwitchAsync(Settings.Switcher); }
        catch (Exception ex) { ShowBalloon($"切替中にエラーが発生しました: {ex.Message}"); }
    }

    private async Task DoGestureAsync(GestureAction action, IntPtr target)
    {
        if (_switcher is null || !Settings.SwitcherEnabled) return;
        try
        {
            switch (action)
            {
                case GestureAction.Hide: await _switcher.HideAsync(Settings.Switcher); break;
                case GestureAction.Restore: _switcher.RestoreStashedWindow(); break;
                case GestureAction.Switch: await _switcher.SwitchAsync(Settings.Switcher); break;
                case GestureAction.BringTarget: await _switcher.BringTargetAsync(Settings.Switcher); break;
                case GestureAction.MinimizeWindow:
                case GestureAction.ToggleMaximizeWindow:
                case GestureAction.CloseWindow:
                    WindowSwitcher.WindowCommand(target, action);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowBalloon($"ジェスチャの実行中にエラーが発生しました: {ex.Message}");
        }
    }

    private void RestoreHidden()
    {
        if (_switcher is null) return;
        bool ok = _switcher.RestoreStashedWindow();
        ShowBalloon(ok ? "隠したウインドウを戻しました。" : "戻せる隠しウインドウはありません。");
    }

    private void OpenSwitcherSettings()
    {
        if (_switcherWindow is not null) { _switcherWindow.Activate(); return; }
        _switcherWindow = new SwitcherSettingsWindow(Settings.Switcher.Clone());
        _switcherWindow.Closed += (_, _) => _switcherWindow = null;
        _switcherWindow.SettingsSaved += s =>
        {
            Settings.Switcher = s;
            SaveSettings();
            ApplySwitcher();
            ShowBalloon("切替の設定を保存しました。");
        };
        _switcherWindow.Show();
        _switcherWindow.Activate();
    }

    // ---------- トレイ ----------

    private void InitTrayIcon()
    {
        _menu = new TrayMenu(() => Settings, () => History);
        _menu.OpenSettingsRequested += () => OpenSettings(null);
        _menu.ManagePinsRequested += app => OpenSettings(app.Id);
        _menu.EditPinRequested += EditPin;
        _menu.SettingsChanged += SaveSettings;
        _menu.HistoryChanged += SaveHistory;
        _menu.ExitRequested += Shutdown;
        _menu.Notify += ShowBalloon;

        _trayIconImage = IconFactory.CreateTrayIcon();
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIconImage,
            Visible = true,
            Text = "LunchBox",
        };
        // 既定：左クリック＝検索ウインドウ／右クリック＝メニュー。設定で入れ替えできる。
        // （ContextMenuStrip を NotifyIcon に常時割り当てると右クリックで必ずメニューが出てしまうので、自前で出し分ける）
        _trayIcon.MouseUp += (_, e) =>
        {
            bool left = e.Button == WinForms.MouseButtons.Left;
            bool right = e.Button == WinForms.MouseButtons.Right;
            if (!left && !right) return;
            bool menu = Settings.SwapTrayClicks ? left : right;
            if (menu) ShowTrayMenu(); else ShowSearch();
        };
    }

    /// <summary>NotifyIcon の内部メソッドでメニューを出す（前面化と外側クリックで閉じる処理込み）</summary>
    private void ShowTrayMenu()
    {
        if (_trayIcon is null) return;
        try
        {
            var mi = typeof(WinForms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi is not null && _menu is not null)
            {
                // 表示の瞬間だけ割り当てる（常時割り当てると右クリックで必ずメニューになる）
                _trayIcon.ContextMenuStrip = _menu.Strip;
                try { mi.Invoke(_trayIcon, null); }
                finally { _trayIcon.ContextMenuStrip = null; }
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.Log("ShowContextMenu 呼び出し失敗: " + ex.Message);
        }
        // フォールバック
        if (_menu is not null)
        {
            NativeMethods.SetForegroundWindow(_menu.Strip.Handle);
            _menu.Strip.Show(WinForms.Cursor.Position);
        }
    }

    // ---------- 「最近」の自動収集 ----------

    private void InitHistoryCollection()
    {
        IconCache.ChromeProfile = Settings.ChromeProfile;
        History.MaxPerApp = Settings.HistoryMaxPerApp;

        _collector = new HistoryCollector(() => Settings, History);
        _collector.Changed += SaveHistory;
        _collector.CollectInBackground();

        _explorerWatcher = new ExplorerWatcher(() => Settings, History);
        _explorerWatcher.Changed += SaveHistory;
        _explorerWatcher.Start();

        // 定期収集（1分ごと）。収集元の更新日時が前回と同じなら読み飛ばす（10 回に 1 回は全部読み直す）
        _collectTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _collectTimer.Tick += (_, _) => _collector.CollectInBackground();
        _collectTimer.Start();

        // メニューを開く直前にも、前回から 10 秒以上たっていれば裏で収集（表示は現在の内容、次回に反映）。
        // エクスプローラーの列挙（COM）はメニューが表示されてから回す＝開く処理を遅らせない
        if (_menu is not null)
            _menu.Strip.Opening += (_, _) =>
            {
                // メニューを開くときは、目印（更新日時）が同じでも全部読み直す＝これまでどおりの動き
                if (_collector.IsStale(TimeSpan.FromSeconds(10))) _collector.CollectInBackground(force: true);
                Dispatcher.BeginInvoke(new Action(() => _explorerWatcher?.Poll()), DispatcherPriority.Background);
            };

        // 起動の 2 秒後に一度メニューを裏で組み立ててキャッシュを温める（初回の右クリックで閉じてしまう対策）
        var warm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        warm.Tick += (_, _) =>
        {
            warm.Stop();
            _menu?.Warm();
        };
        warm.Start();

        // メモリ整理：起動 20 秒後に 1 回（起動時の一時データを返す）、以後 5 分ごと
        var trimOnce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        trimOnce.Tick += (_, _) => { trimOnce.Stop(); MemoryTrim.Run("起動後"); };
        trimOnce.Start();
        var trim = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        trim.Tick += (_, _) => MemoryTrim.Run("定期");
        trim.Start();
    }

    private void SaveHistory()
    {
        lock (_historySaveGate)
            History.Save();
    }

    // ---------- クリップボード履歴 ----------

    private void InitClipboard()
    {
        Clips = ClipStore.Load(Settings.ClipboardPersist);
        ApplyClipArchiveSettings();
        _clipboard = new ClipboardMonitor(() => Settings, Clips);
        _clipboard.Changed += SaveClips;
        _clipboard.Apply(Settings);
        if (_menu is not null)
        {
            _menu.Clips = Clips;
            _menu.Clipboard = _clipboard;
            _menu.ClipsChanged += SaveClips;
            _menu.OpenClipboardSettingsRequested += () => OpenSettings(null, "クリップボード");
        }
    }

    private void ApplyClipArchiveSettings()
    {
        Clips.ArchiveEnabled = Settings.ClipboardArchiveOverflow;
        Clips.ArchiveFolder = Settings.ClipboardArchiveFolder ?? "";
    }

    private void SaveClips()
    {
        lock (_clipsSaveGate)
            Clips.Save(Settings.ClipboardPersist);
    }

    private void ClearClips(ClipKind? kind)
    {
        int n = Clips.Clear(kind);
        SaveClips();
        Logger.Log($"クリップボード履歴クリア: {kind?.ToString() ?? "all"} {n}件");
        ShowBalloon($"クリップボード履歴を {n} 件消しました。");
    }

    // ---------- 検索ウインドウ ----------

    private void ShowSearch()
    {
        try
        {
            if (_search is null)
            {
                _search = new SearchWindow(() => Settings, () => History, () => Clips, () => _clipboard);
                _search.PinOpened += (_, _) => SaveSettings();
            }
            _search.ShowNearCursor();
        }
        catch (Exception ex)
        {
            Logger.Log("検索ウインドウの表示失敗: " + ex.Message);
            ShowTrayMenu();
        }
    }

    // ---------- ホットキー ----------

    private void InitHotKey()
    {
        _hotKeys = new HotKeyService();
        _hotKeys.HotKeyPressed += () =>
        {
            if (Settings.HotKeyOpensSearch) ShowSearch(); else ShowTrayMenu();
        };
        RegisterCurrentHotKey();
    }

    public void RegisterCurrentHotKey()
    {
        if (_hotKeys is null) return;
        if (!Settings.HotKeyEnabled)
        {
            _hotKeys.Unregister();
            return;
        }
        bool ok = _hotKeys.Register(Settings.HotKeyModifiers, Settings.HotKeyVirtualKey);
        if (!ok)
        {
            ShowBalloon($"ホットキー [{HotKeyText.Format(Settings.HotKeyModifiers, Settings.HotKeyVirtualKey)}] を登録できませんでした。" +
                        "他のアプリと競合している可能性があります。設定で別のキーをお試しください。");
        }
    }

    // ---------- 設定画面・編集 ----------

    private void OpenSettings(string? selectAppId, string? tab = null)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.SelectApp(selectAppId);
            if (tab is not null) _settingsWindow.SelectTab(tab);
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Settings.Clone(), selectAppId);
        _settingsWindow.GetClipCounts = () => (Clips.Count(ClipKind.Text), Clips.Count(ClipKind.Image));
        _settingsWindow.ClearClipsRequested += ClearClips;
        if (tab is not null) _settingsWindow.SelectTab(tab);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _settingsWindow.OpenSwitcherSettingsRequested += OpenSwitcherSettings;
        _settingsWindow.ClearHistoryRequested += appId =>
        {
            int n = appId is null ? History.ClearAll() : History.Clear(appId);
            // 次の定期収集で、収集元が変わっていなくても読み直す（クリア後の動きをこれまでと同じにする）
            _collector?.Invalidate();
            SaveHistory();
            Logger.Log($"履歴クリア: app={appId ?? "(all)"} {n}件");
            ShowBalloon($"履歴を {n} 件消しました。");
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        // 切替の設定は別ウインドウで即時保存されるので、この画面の古い複製で上書きしない
        newSettings.Switcher = Settings.Switcher;
        newSettings.SwitcherMigrated = Settings.SwitcherMigrated;
        newSettings.FrontSwitcherTaskRemoved = Settings.FrontSwitcherTaskRemoved;
        Settings = newSettings;
        Settings.Sanitize();
        SaveSettings();
        StartupManager.Apply(Settings.StartWithWindows, Settings.RunElevated);
        ApplySwitcher();
        RegisterCurrentHotKey();
        ApplyLayoutSettings();
        ApplyCalendarSettings();
        ApplyNotifyPlacer();
        StartLinkCheck();
        IconCache.ChromeProfile = Settings.ChromeProfile;
        History.MaxPerApp = Settings.HistoryMaxPerApp;
        _collector?.CollectInBackground(force: true);
        _explorerWatcher?.Poll();
        ApplyClipArchiveSettings();
        _clipboard?.Apply(Settings);
        if (!Settings.ClipboardPersist)
        {
            try { System.IO.File.Delete(ClipStore.StorePath); } catch { }
        }
        else
        {
            SaveClips();
        }
        ShowBalloon("設定を保存しました。");
        if (Settings.RunElevated != Elevation.IsElevated) RestartForElevationChange();
    }

    private void EditPin(AppEntry app, PinItem pin)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            MessageBox.Show("設定画面を開いている間は、設定画面の「ピン留め」タブから編集してください。",
                AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new EditItemWindow(pin.Clone(), app.Name);
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            int idx = app.Pins.IndexOf(pin);
            if (idx >= 0) app.Pins[idx] = dlg.Result;
            else app.Pins.Add(dlg.Result);
            SaveSettings();
        }
    }

    public void SaveSettings()
    {
        lock (_saveGate)
            Settings.Save();
    }

    // ---------- リンク確認 ----------

    private void StartLinkCheck()
    {
        _linkCheckTimer?.Stop();
        _linkCheckTimer = null;
        _ = Task.Run(CheckAllLinks);
        if (Settings.LinkCheckIntervalMinutes > 0)
        {
            _linkCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Settings.LinkCheckIntervalMinutes) };
            _linkCheckTimer.Tick += (_, _) => _ = Task.Run(CheckAllLinks);
            _linkCheckTimer.Start();
        }
    }

    private void CheckAllLinks()
    {
        try
        {
            bool changed = false;
            int notFound = 0;
            foreach (var app in Settings.Apps.ToList())
            {
                foreach (var pin in app.Pins.ToList())
                {
                    var before = (pin.Status, pin.Target);
                    var st = LinkResolver.Check(pin);
                    if (st == LinkStatus.NotFound) notFound++;
                    if (before.Status != pin.Status || before.Target != pin.Target) changed = true;
                }
            }
            if (changed) SaveSettings();
            Logger.Log($"リンク確認 完了: 見つからない={notFound}");
        }
        catch (Exception ex)
        {
            Logger.Log("リンク確認エラー: " + ex.Message);
        }
    }

    // ---------- 共通 ----------

    private void ShowBalloon(string message)
    {
        _trayIcon?.ShowBalloonTip(4000, AppSettings.AppName, message, WinForms.ToolTipIcon.Info);
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _linkCheckTimer?.Stop();
        _collectTimer?.Stop();
        _explorerWatcher?.Dispose();
        _clipboard?.Dispose();
        _hotKeys?.Dispose();
        _switchHotKey?.Dispose();
        _gestures?.Dispose();
        _notifyPlacer?.Dispose();
        _holidayTimer?.Stop();
        _calendarHotKey?.Dispose();
        try { _calendarWindow?.Dispose(); } catch { }
        _layoutRestoreHotKey?.Dispose();
        _layoutRecallHotKey?.Dispose();
        _layouts?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _menu?.Dispose();
        _trayIconImage?.Dispose();
        if (_ownsMutex) { try { _singleInstanceMutex?.ReleaseMutex(); } catch { } }
        _singleInstanceMutex?.Dispose();
        Logger.Log("=== 終了 ===");
    }
}
