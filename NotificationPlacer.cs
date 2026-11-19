using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace LunchBox;

/// <summary>
/// Windows の通知バナー（トースト）を、指定したモニタの右下に出す。
/// 通知は ShellExperienceHost の専用ウインドウ 1 枚（クラス Windows.UI.Core.CoreWindow・日本語版のタイトル「新しい通知」）で出ており、
/// 外から位置を動かすと以後の通知もそこに出る。Windows の非公開の仕組みなので、うまくいかないときは何もしない側に倒す。
/// 依頼：docs\仕様書プロンプト_通知の表示モニター_R8.11.16.md
/// </summary>
internal sealed class NotificationPlacer : IDisposable
{
    /// <summary>表示先：0=指定したモニタ 1=マウスカーソルのあるモニタ 2=前面のウインドウがあるモニタ</summary>
    public const int ModeMonitor = 0, ModeCursor = 1, ModeForeground = 2;

    private const string ToastClass = "Windows.UI.Core.CoreWindow";
    private const string ToastTitleJa = "新しい通知";
    private const int MaxFailures = 10;
    private const int MaxMovesIn10s = 30;

    private readonly record struct Mon(string Key, string Label, N.RECT Bounds, N.RECT Work, bool Primary, IntPtr Handle);

    private bool _enabled;
    private int _mode;
    private string _key = "";
    private IntPtr _hwnd;
    private uint _hookPid;
    private IntPtr _locHook, _fgHook;
    private readonly N.WinEventProc _locProc, _fgProc;
    private readonly DispatcherTimer _periodic, _cursorTimer, _delay, _watch;
    private N.RECT _seen;          // 最後に確認した通知ウインドウの位置（見張り用）
    private bool _watchLogged;
    private string _delayReason = "";
    private bool _subscribed;
    private IntPtr _lastCursorMon, _lastTarget;
    private string _lastPlacedKey = "";
    private int _failures;
    private readonly Queue<DateTime> _moves = new();
    // メインモニタでの元の位置（作業領域の右端・下端からの距離）。最初にメインモニタ上で見つけたときに記録する
    private int _homeDx, _homeDy;
    private bool _homeKnown;
    private string _lastLoggedStatus = "";

    /// <summary>設定画面に出す状態</summary>
    public string Status { get; private set; } = "オフ（Windows の位置のまま）";
    /// <summary>失敗が続いて自動で止めた</summary>
    public bool AutoStopped { get; private set; }

    /// <summary>動かしたままのときに置く目印（不正終了のあと、次の起動で元へ戻すため）</summary>
    private static string MarkerPath => Path.Combine(AppSettings.SettingsDirectory, "notify_moved.flag");

    public NotificationPlacer()
    {
        _locProc = OnLocationChange;
        _fgProc = OnForeground;
        _periodic = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _periodic.Tick += (_, _) => Place("定期確認");
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cursorTimer.Tick += (_, _) => CheckCursor();
        _delay = new DispatcherTimer();
        _delay.Tick += (_, _) => { _delay.Stop(); Place(_delayReason); };
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _watch.Tick += (_, _) => Watch();
    }

    // ---------- 設定 ----------

    public void Apply(bool enabled, int mode, string monitorKey)
    {
        _mode = mode;
        _key = monitorKey ?? "";
        if (!enabled)
        {
            bool wasOn = _enabled;
            _enabled = false;
            Stop();
            if (wasOn || File.Exists(MarkerPath)) RestoreOriginal(wasOn ? "オフにした" : "前回の終了時に戻せていなかった");
            AutoStopped = false;
            SetStatus("オフ（Windows の位置のまま）", log: wasOn);
            return;
        }
        _enabled = true;
        AutoStopped = false;
        _failures = 0;
        _moves.Clear();
        _lastPlacedKey = "";
        Start();
        Place("設定");
    }

    /// <summary>設定画面用：いまつながっているモニタの一覧（左から）</summary>
    public static List<(string Key, string Label)> ListMonitors() => Monitors().Select(m => (m.Key, m.Label)).ToList();

    private void Start()
    {
        if (!_subscribed)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _subscribed = true;
        }
        _periodic.Start();
        _watch.Start();
        if (_mode == ModeCursor) { _lastCursorMon = IntPtr.Zero; _cursorTimer.Start(); } else _cursorTimer.Stop();
        if (_mode == ModeForeground)
        {
            if (_fgHook == IntPtr.Zero)
                _fgHook = N.SetWinEventHook(N.EVENT_SYSTEM_FOREGROUND, N.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _fgProc, 0, 0, N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);
        }
        else Unhook(ref _fgHook);
    }

    private void Stop()
    {
        if (_subscribed)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            _subscribed = false;
        }
        _periodic.Stop(); _cursorTimer.Stop(); _delay.Stop(); _watch.Stop();
        Unhook(ref _locHook);
        Unhook(ref _fgHook);
        _hookPid = 0;
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook != IntPtr.Zero) { N.UnhookWinEvent(hook); hook = IntPtr.Zero; }
    }

    // ---------- きっかけ ----------

    private void Later(int ms, string reason)
    {
        if (!_enabled || AutoStopped) return;
        _delayReason = reason;
        _delay.Stop();
        _delay.Interval = TimeSpan.FromMilliseconds(ms);
        _delay.Start();
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Later(2500, "画面構成の変化");

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Later(4000, "スリープ復帰");
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.SessionLogon) Later(3000, "ロック解除");
    }

    private void OnLocationChange(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0) return;            // ウインドウ本体の移動・サイズ変更だけ
        if (_hwnd != IntPtr.Zero && hwnd != _hwnd) return;    // 通知以外（通知センターなど）は見ない
        if (_delay.IsEnabled) return;
        Later(_hwnd == IntPtr.Zero ? 300 : 40, "位置の変化");
    }

    private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev != N.EVENT_SYSTEM_FOREGROUND && ev != N.EVENT_SYSTEM_MOVESIZEEND) return;
        if (hwnd == _hwnd) return;
        Later(150, "前面ウインドウの変化");
    }

    private void CheckCursor()
    {
        if (!N.GetCursorPos(out var p)) return;
        var mon = N.MonitorFromPoint(p, N.MONITOR_DEFAULTTONEAREST);
        if (mon == _lastCursorMon) return;
        _lastCursorMon = mon;
        Place("カーソルの移動");
    }

    // ---------- 通知ウインドウを探す ----------

    private static bool IsShellHost(IntPtr h, out uint pid)
    {
        N.GetWindowThreadProcessId(h, out pid);
        if (pid == 0) return false;
        IntPtr hp = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(520);
            int size = sb.Capacity;
            return N.QueryFullProcessImageName(hp, 0, sb, ref size)
                   && sb.ToString().EndsWith("\\ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally { N.CloseHandle(hp); }
    }

    private static IntPtr Find(out uint pid)
    {
        IntPtr h = N.FindWindow(ToastClass, ToastTitleJa);
        if (h != IntPtr.Zero && IsShellHost(h, out pid)) return h;
        // タイトルは表示言語で変わる。見つからなければ、ShellExperienceHost の CoreWindow のうち通知らしい大きさのものを探す
        h = IntPtr.Zero;
        for (int i = 0; i < 200; i++)
        {
            h = N.FindWindowEx(IntPtr.Zero, h, ToastClass, null);
            if (h == IntPtr.Zero) break;
            if (!IsShellHost(h, out pid) || !N.GetWindowRect(h, out var r)) continue;
            double scale = Math.Max(1.0, N.GetDpiForWindow(h) / 96.0);
            double w = (r.Right - r.Left) / scale, hh = (r.Bottom - r.Top) / scale;
            if (w is >= 300 and <= 500 && hh is >= 60 and <= 330) return h;
        }
        pid = 0;
        return IntPtr.Zero;
    }

    private IntPtr EnsureWindow()
    {
        uint pid;
        if (_hwnd != IntPtr.Zero && N.IsWindow(_hwnd)) N.GetWindowThreadProcessId(_hwnd, out pid);
        else _hwnd = Find(out pid);
        // 知らせの受け口は、無ければ必ず付け直す（オフ→オンで外したままになり、通知が出た瞬間の戻りを追えなかった：11/19）
        if (_hwnd != IntPtr.Zero && _enabled && pid != 0 && (pid != _hookPid || _locHook == IntPtr.Zero))
        {
            // ShellExperienceHost に限定して、ウインドウの移動・サイズ変更を受ける
            Unhook(ref _locHook);
            _locHook = N.SetWinEventHook(N.EVENT_OBJECT_LOCATIONCHANGE, N.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locProc, pid, 0, N.WINEVENT_OUTOFCONTEXT);
            _hookPid = pid;
            Logger.Log($"通知: 通知ウインドウを見つけました（pid={pid}・知らせの受け口={(_locHook != IntPtr.Zero ? "あり" : "付けられず＝見張りだけで動かします")}）");
        }
        return _hwnd;
    }

    /// <summary>
    /// 知らせ（WinEvent）が届かないときの保険。通知ウインドウの位置を読むだけの軽い見張りで、変わっていたら置き直す。
    /// 位置が変わらない間は GetWindowRect 1 回だけで、メモリの割り当てはない。
    /// </summary>
    private void Watch()
    {
        if (!_enabled || AutoStopped || _delay.IsEnabled) return;
        if (_hwnd == IntPtr.Zero || !N.IsWindow(_hwnd) || !N.GetWindowRect(_hwnd, out var r)) return;
        if (r.Left == _seen.Left && r.Top == _seen.Top && r.Right == _seen.Right && r.Bottom == _seen.Bottom) return;
        _seen = r;
        if (!_watchLogged) { _watchLogged = true; Logger.Log("通知: 知らせが届かなかったので、見張りで位置の変化を拾いました（以後は記録しません）"); }
        Place("見張り");
    }

    // ---------- モニタ ----------

    private static List<Mon> Monitors()
    {
        var raw = new List<(N.RECT Bounds, N.RECT Work, bool Primary, IntPtr Handle, string Model)>();
        N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref N.RECT _, IntPtr _) =>
        {
            var mi = new N.MONITORINFOEX { cbSize = Marshal.SizeOf<N.MONITORINFOEX>() };
            if (N.GetMonitorInfo(hMon, ref mi))
            {
                string model = "";
                var dd = new N.DISPLAY_DEVICE { cb = Marshal.SizeOf<N.DISPLAY_DEVICE>() };
                if (N.EnumDisplayDevices(mi.szDevice, 0, ref dd, 0))
                {
                    var parts = (dd.DeviceID ?? "").Split('\\');
                    if (parts.Length > 1) model = parts[1];
                }
                raw.Add((mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, hMon, model));
            }
            return true;
        }, IntPtr.Zero);
        raw = raw.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        var list = new List<Mon>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var m = raw[i];
            int w = m.Bounds.Right - m.Bounds.Left, h = m.Bounds.Bottom - m.Bounds.Top;
            string pos = raw.Count switch
            {
                1 => "",
                2 => i == 0 ? "左" : "右",
                3 => i == 0 ? "左" : i == 1 ? "中央" : "右",
                _ => $"左から{i + 1}番目",
            };
            string label = (pos + (m.Primary ? (pos.Length > 0 ? "（メイン）" : "メイン") : "") + $"　{w}×{h}").Trim('　');
            // ウインドウ配置の記憶と同じ見分け方：機種＋解像度＋位置
            list.Add(new Mon($"{m.Model}:{w}x{h}@{m.Bounds.Left},{m.Bounds.Top}", label, m.Bounds, m.Work, m.Primary, m.Handle));
        }
        return list;
    }

    private Mon? PickTarget(List<Mon> mons, IntPtr toast)
    {
        IntPtr hMon;
        switch (_mode)
        {
            case ModeCursor:
                if (!N.GetCursorPos(out var p)) return null;
                hMon = N.MonitorFromPoint(p, N.MONITOR_DEFAULTTONEAREST);
                break;
            case ModeForeground:
                IntPtr fg = N.GetForegroundWindow();
                // 前面が無い・通知そのものが前面のときは、前回のモニタのまま
                hMon = fg == IntPtr.Zero || fg == toast ? _lastTarget : N.MonitorFromWindow(fg, N.MONITOR_DEFAULTTONEAREST);
                if (hMon == IntPtr.Zero) hMon = mons.FirstOrDefault(m => m.Primary).Handle;
                break;
            default:
                if (_key.Length == 0)
                {
                    // モニタを選ばずにオンにしたとき（メニューからの切り替えなど）：メイン以外で一番右のモニタ
                    for (int i = mons.Count - 1; i >= 0; i--) if (!mons[i].Primary) return mons[i];
                    return null;
                }
                foreach (var m in mons) if (m.Key == _key) return m;
                return null;   // 指定したモニタが外れている
        }
        foreach (var m in mons) if (m.Handle == hMon) return m;
        return null;
    }

    // ---------- 置く・戻す ----------

    private void Place(string reason)
    {
        if (!_enabled || AutoStopped) return;
        try { PlaceCore(reason); }
        finally
        {
            // 置いた後の位置を覚えておく（見張りが自分の移動を「変化」と数えないように）
            if (_hwnd != IntPtr.Zero && N.GetWindowRect(_hwnd, out var now)) _seen = now;
        }
    }

    private void PlaceCore(string reason)
    {
        try
        {
            IntPtr h = EnsureWindow();
            if (h == IntPtr.Zero)
            {
                SetStatus("通知ウインドウがまだ見つかりません（通知が出るようになると動かせます）", log: true);
                return;
            }
            if (!N.GetWindowRect(h, out var r)) { Fail("通知ウインドウの位置を読めません"); return; }
            var mons = Monitors();
            if (mons.Count == 0) return;
            var primary = mons.FirstOrDefault(m => m.Primary);
            if (primary.Handle == IntPtr.Zero) primary = mons[0];
            RecordHome(r, primary);

            var target = PickTarget(mons, h);
            if (target is null)
            {
                RestoreOriginal("表示先のモニタがつながっていない");
                SetStatus("表示先のモニタがつながっていないので、Windows の位置（メインモニタ）に出しています", log: true);
                _lastPlacedKey = "";
                return;
            }
            var t = target.Value;
            _lastTarget = t.Handle;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            int x = t.Work.Right - w, y = t.Work.Bottom - hh;
            if (r.Left == x && r.Top == y)
            {
                _failures = 0;
                Placed(t, x, y, reason, moved: false);
                return;
            }
            if (!AllowMove()) return;

            bool ok = Move(h, x, y);
            int err = Marshal.GetLastWin32Error();
            // 拡大率の違うモニタへ移ると大きさが変わることがある。読み直して、ずれていたらもう一度合わせる
            if (ok && N.GetWindowRect(h, out var r2) && (r2.Right - r2.Left != w || r2.Bottom - r2.Top != hh))
            {
                w = r2.Right - r2.Left; hh = r2.Bottom - r2.Top;
                x = t.Work.Right - w; y = t.Work.Bottom - hh;
                ok = Move(h, x, y);
                err = Marshal.GetLastWin32Error();
            }
            if (ok && N.GetWindowRect(h, out var r3) && r3.Left == x && r3.Top == y)
            {
                _failures = 0;
                try { if (!File.Exists(MarkerPath)) File.WriteAllText(MarkerPath, DateTime.Now.ToString("s")); } catch { }
                Placed(t, x, y, reason, moved: true);
            }
            else Fail($"通知ウインドウを動かせません（err={err}）");
        }
        catch (Exception ex)
        {
            Fail("通知の置き直しでエラー: " + ex.Message);
        }
    }

    private void Placed(Mon t, int x, int y, string reason, bool moved)
    {
        // ログは表示先のモニタが変わったときだけ（高さの変化による置き直しは記録しない）
        if (t.Key != _lastPlacedKey)
        {
            _lastPlacedKey = t.Key;
            Logger.Log($"通知: 表示先＝{t.Label} の右下（{x},{y}）{(moved ? "へ移動" : "に配置済み")}／きっかけ＝{reason}");
        }
        Status = $"{t.Label} の右下に出しています";
        _lastLoggedStatus = Status;
    }

    private static bool Move(IntPtr h, int x, int y)
        => N.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, N.SWP_NOSIZE | N.SWP_NOZORDER | N.SWP_NOACTIVATE);

    /// <summary>短い時間に何度も動かすことになったら、Windows が位置を戻し続けているとみて止める</summary>
    private bool AllowMove()
    {
        var now = DateTime.Now;
        while (_moves.Count > 0 && (now - _moves.Peek()).TotalSeconds > 10) _moves.Dequeue();
        _moves.Enqueue(now);
        if (_moves.Count <= MaxMovesIn10s) return true;
        AutoStop("Windows が通知の位置を戻し続けています");
        return false;
    }

    private void Fail(string message)
    {
        _failures++;
        if (_failures <= 3 || _failures == MaxFailures) Logger.Log($"通知: {message}（連続 {_failures} 回目）");
        Status = message;
        if (_failures >= MaxFailures) AutoStop(message);
    }

    private void AutoStop(string why)
    {
        AutoStopped = true;
        Stop();
        RestoreOriginal("自動停止");
        Status = $"うまく動かせないため自動で止めました：{why}（設定を保存し直すか、LunchBox を起動し直すと再開します）";
        Logger.Log("通知: " + Status);
    }

    private void RecordHome(N.RECT r, Mon primary)
    {
        if (_homeKnown) return;
        // メインモニタの作業領域の中にあるときだけ「元の位置」とみなす（前回の終了時に戻せていなかった場合を除くため）
        if (r.Left < primary.Bounds.Left || r.Right > primary.Bounds.Right || r.Top < primary.Bounds.Top || r.Bottom > primary.Bounds.Bottom) return;
        int dx = primary.Work.Right - r.Right, dy = primary.Work.Bottom - r.Bottom;
        if (dx is < 0 or > 200 || dy is < 0 or > 200) return;   // 右下以外にあるなら記録しない（計算値＝右下ぴったりを使う）
        _homeDx = dx; _homeDy = dy; _homeKnown = true;
    }

    /// <summary>メインモニタの元の位置へ戻す。オフにしたとき・終了時・表示先が外れたとき・自動停止・不正終了の処理から呼ぶ</summary>
    public void RestoreOriginal(string reason)
    {
        try
        {
            IntPtr h = _hwnd != IntPtr.Zero && N.IsWindow(_hwnd) ? _hwnd : Find(out _);
            if (h == IntPtr.Zero || !N.GetWindowRect(h, out var r)) return;
            var mons = Monitors();
            var primary = mons.FirstOrDefault(m => m.Primary);
            if (primary.Handle == IntPtr.Zero) return;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            int x = primary.Work.Right - _homeDx - w, y = primary.Work.Bottom - _homeDy - hh;
            bool moved = false;
            if (r.Left != x || r.Top != y)
            {
                Move(h, x, y);
                // 拡大率が違うと大きさが変わるので、読み直してもう一度
                if (N.GetWindowRect(h, out var r2) && (r2.Right - r2.Left != w || r2.Bottom - r2.Top != hh))
                    Move(h, primary.Work.Right - _homeDx - (r2.Right - r2.Left), primary.Work.Bottom - _homeDy - (r2.Bottom - r2.Top));
                moved = true;
            }
            try { File.Delete(MarkerPath); } catch { }
            if (moved) Logger.Log($"通知: メインモニタの元の位置（{x},{y}）へ戻しました／理由＝{reason}");
        }
        catch (Exception ex)
        {
            try { Logger.Log("通知: 元の位置へ戻せませんでした: " + ex.Message); } catch { }
        }
    }

    private void SetStatus(string status, bool log)
    {
        Status = status;
        if (log && status != _lastLoggedStatus) { _lastLoggedStatus = status; Logger.Log("通知: " + status); }
    }

    public void Dispose()
    {
        bool wasOn = _enabled;
        _enabled = false;
        Stop();
        if (wasOn || File.Exists(MarkerPath)) RestoreOriginal("LunchBox の終了");
    }

    // ---------- Win32 ----------

    private static class N
    {
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const uint SWP_NOSIZE = 1, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data);
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr hp, int flags, StringBuilder sb, ref int size);
    }
}
