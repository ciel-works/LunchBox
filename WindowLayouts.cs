using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;

namespace LunchBox;

// ウインドウ配置の記憶（設計：docs\設計案_ウインドウ配置の記憶_R8.11.13.md）
// ・配置は「画面構成」（モニタの台数・機種・解像度・位置）ごとに持つ
// ・Live＝いまの構成で記録中の配置／Previous＝この構成を前回離れたときの配置（直前の配置。3 世代）／Saves＝手動セーブ

public sealed class LayoutMonitor
{
    public string Model { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Primary { get; set; }
}

public sealed class LayoutWindow
{
    public long Hwnd { get; set; }
    public string Exe { get; set; } = "";
    /// <summary>ストアアプリなどのアプリ ID（起動に使う）。無ければ空</summary>
    public string AppId { get; set; } = "";
    /// <summary>管理者権限で動いていた（起動して配置する対象から外す）</summary>
    public bool Elevated { get; set; }
    public string Class { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>0=通常 1=最大化 2=最小化</summary>
    public int State { get; set; }
    /// <summary>最小化中で、戻すと最大化になる</summary>
    public bool RestoreToMax { get; set; }
    /// <summary>通常時の位置（left, top, right, bottom。作業領域の座標）</summary>
    public int[] Normal { get; set; } = new int[4];
    /// <summary>見えている位置（left, top, right, bottom。画面の座標）</summary>
    public int[] Rect { get; set; } = new int[4];
    /// <summary>重なりの順番（0 が最前面）</summary>
    public int Z { get; set; }

    public bool SamePlace(LayoutWindow o)
    {
        if (State != o.State) return false;
        if (State == 2) return RestoreToMax == o.RestoreToMax && Normal.SequenceEqual(o.Normal);
        return Rect.SequenceEqual(o.Rect);
    }
}

public sealed class LayoutSnapshot
{
    public string Name { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime When { get; set; }
    public DateTime Seen { get; set; }
    public List<LayoutWindow> Windows { get; set; } = new();

    public bool SameAs(List<LayoutWindow> other)
    {
        if (Windows.Count != other.Count) return false;
        for (int i = 0; i < other.Count; i++)
        {
            var a = Windows[i]; var b = other[i];
            if (a.Hwnd != b.Hwnd || a.Title != b.Title || !a.SamePlace(b)) return false;
        }
        return true;
    }
}

public sealed class LayoutConfig
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public List<LayoutMonitor> Monitors { get; set; } = new();
    public LayoutSnapshot? Live { get; set; }
    public List<LayoutSnapshot> Previous { get; set; } = new();
    public List<LayoutSnapshot> Saves { get; set; } = new();
}

public sealed class LayoutFile
{
    public List<LayoutConfig> Configs { get; set; } = new();
}

public readonly record struct LayoutResult(int Moved, int Same, int Missing, int Failed, int Launching = 0)
{
    public string Counts
    {
        get
        {
            var sb = new StringBuilder($"動かした {Moved}・そのまま {Same}");
            if (Launching > 0) sb.Append($"・起動中 {Launching}");
            if (Missing > 0) sb.Append($"・開いていない {Missing}");
            if (Failed > 0) sb.Append($"・動かせなかった {Failed}");
            return sb.ToString();
        }
    }

    public string Message
    {
        get
        {
            return "配置を戻しました：" + Counts;
        }
    }
}

public sealed class WindowLayoutService : IDisposable
{
    private const int MaxPrevious = 3;
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinSession = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private LayoutFile _file = new();
    private string _currentId = "";
    private DateTime _suspendUntil = DateTime.MinValue;
    private bool _dirty;
    private DateTime _lastWrite = DateTime.MinValue;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _periodic;
    private readonly DispatcherTimer _settle;
    private IntPtr _hook;
    private readonly L.WinEventProc _hookProc;
    private readonly Dictionary<uint, (string Exe, string AppId, bool Elevated)> _procCache = new();
    private readonly DispatcherTimer _secondPass;
    private LayoutSnapshot? _secondPassSnap;
    private string _secondPassId = "";
    private bool _started;
    // 画面の切り替わりが始まった時点の配置（構成が同じまま戻ってきたときに、ずれを直すため）
    private List<LayoutWindow>? _frozen;
    private string _frozenId = "";
    private DateTime _frozenWhen;

    /// <summary>自動記録を使う（OFF でも手動セーブ／呼び出しは使える）</summary>
    public bool AutoRecord { get; set; } = true;

    /// <summary>つなぎ直したとき：0=自動で直前の配置に戻す 1=通知だけ 2=何もしない</summary>
    public int AutoRestoreMode { get; set; }

    /// <summary>自動復元やアプリ起動の結果を知らせる</summary>
    public event Action<string>? Notify;

    public WindowLayoutService(string path)
    {
        _path = path;
        Load();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Record(); };
        _periodic = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _periodic.Tick += (_, _) => Record();
        _settle = new DispatcherTimer { Interval = SettleTime + TimeSpan.FromMilliseconds(500) };
        _settle.Tick += (_, _) => { _settle.Stop(); Record(); };

        _secondPass = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _secondPass.Tick += (_, _) =>
        {
            // Windows 側の並べ直しが後から来ることがあるので、もう一度だけ合わせる
            _secondPass.Stop();
            var snap = _secondPassSnap;
            _secondPassSnap = null;
            if (snap is not null && _secondPassId == _currentId && DateTime.Now >= _suspendUntil - TimeSpan.FromSeconds(2))
            {
                try { Restore(snap); } catch (Exception ex) { Logger.Log("ウインドウ配置の再調整に失敗: " + ex.Message); }
            }
        };

        EnterConfig(DetectMonitors());
        _started = true;

        _hookProc = OnWinEvent;
        _hook = L.SetWinEventHook(L.EVENT_SYSTEM_FOREGROUND, L.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _hookProc, 0, 0,
                                  L.WINEVENT_OUTOFCONTEXT | L.WINEVENT_SKIPOWNPROCESS);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // 起動直後は他の初期化が落ち着いてから最初の記録
        _suspendUntil = DateTime.Now + SettleTime;
        _settle.Start();
        _periodic.Start();
    }

    // ---------- 画面構成 ----------

    public LayoutConfig Current
    {
        get
        {
            var c = _file.Configs.FirstOrDefault(x => x.Id == _currentId);
            if (c is null)
            {
                var mons = DetectMonitors();
                c = new LayoutConfig { Id = MakeId(mons), Description = Describe(mons), Monitors = mons };
                _currentId = c.Id;
                _file.Configs.Add(c);
            }
            return c;
        }
    }

    public static List<LayoutMonitor> DetectMonitors()
    {
        var list = new List<LayoutMonitor>();
        L.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref L.RECT _, IntPtr _) =>
        {
            var mi = new L.MONITORINFOEX { cbSize = Marshal.SizeOf<L.MONITORINFOEX>() };
            if (L.GetMonitorInfo(hMon, ref mi))
            {
                string model = "";
                var dd = new L.DISPLAY_DEVICE { cb = Marshal.SizeOf<L.DISPLAY_DEVICE>() };
                if (L.EnumDisplayDevices(mi.szDevice, 0, ref dd, 0))
                {
                    // 例：MONITOR\GSM5B09\{4d36e96e-...}\0003 → GSM5B09
                    var parts = (dd.DeviceID ?? "").Split('\\');
                    if (parts.Length > 1) model = parts[1];
                }
                list.Add(new LayoutMonitor
                {
                    Model = model,
                    X = mi.rcMonitor.Left, Y = mi.rcMonitor.Top,
                    W = mi.rcMonitor.Right - mi.rcMonitor.Left, H = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    Primary = (mi.dwFlags & 1) != 0,
                });
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
    }

    private static string MakeId(List<LayoutMonitor> mons)
        => string.Join("|", mons.Select(m => $"{m.Model}:{m.W}x{m.H}@{m.X},{m.Y}"));

    private static string Describe(List<LayoutMonitor> mons)
        => $"{mons.Count}画面（{string.Join("・", mons.Select(m => $"{m.W}×{m.H}"))}）";

    private bool EnterConfig(List<LayoutMonitor> mons)
    {
        string id = MakeId(mons);
        if (id == _currentId) return false;
        _currentId = id;
        var c = _file.Configs.FirstOrDefault(x => x.Id == id);
        if (c is null)
        {
            c = new LayoutConfig { Id = id, Description = Describe(mons), Monitors = mons };
            _file.Configs.Add(c);
        }
        // この構成の前回の記録を「直前の配置」として確定する。
        // 一瞬だけの記録（つなぎ替え途中など）は捨てる。直前と同じなら増やさない。
        var live = c.Live;
        c.Live = null;
        if (live is not null && live.Windows.Count > 0 && live.Seen - live.Started >= MinSession
            && !(c.Previous.Count > 0 && c.Previous[0].SameAs(live.Windows)))
        {
            c.Previous.Insert(0, live);
            while (c.Previous.Count > MaxPrevious) c.Previous.RemoveAt(c.Previous.Count - 1);
        }
        _dirty = true;
        Logger.Log($"ウインドウ配置: 画面構成 {c.Description} / 直前の配置 {c.Previous.Count} 件 / セーブ {c.Saves.Count} 件");
        return true;
    }

    /// <summary>画面構成が変わって落ち着いたとき：設定に従って直前の配置に戻す／知らせる</summary>
    private void AfterReconnect()
    {
        var c = Current;
        if (c.Previous.Count == 0 || AutoRestoreMode == 2) return;
        if (AutoRestoreMode == 1)
        {
            Notify?.Invoke($"画面が {c.Description} になりました。メニューの「ウインドウ配置」→「直前の配置に戻す」で前の配置に戻せます。");
            return;
        }
        var snap = c.Previous[0];
        var r = Restore(snap);
        Notify?.Invoke($"画面が {c.Description} になったので、直前の配置（{snap.When:MM/dd HH:mm}）に戻しました：{r.Counts}");
        _secondPassSnap = snap;
        _secondPassId = _currentId;
        _secondPass.Stop();
        _secondPass.Start();
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        // モニタの抜き差し直後は Windows がウインドウを寄せている最中。落ち着くまで記録しない
        if (_frozen is null)
        {
            var live = _file.Configs.FirstOrDefault(x => x.Id == _currentId)?.Live;
            if (live is not null && live.Windows.Count > 0)
            {
                _frozen = live.Windows;
                _frozenId = _currentId;
                _frozenWhen = live.When;
            }
        }
        _suspendUntil = DateTime.Now + SettleTime;
        _debounce.Stop();
        _secondPass.Stop();
        _settle.Stop();
        _settle.Start();
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev != L.EVENT_SYSTEM_FOREGROUND && ev != L.EVENT_SYSTEM_MOVESIZEEND
            && ev != L.EVENT_SYSTEM_MINIMIZESTART && ev != L.EVENT_SYSTEM_MINIMIZEEND) return;
        if (!AutoRecord) return;
        _debounce.Stop();
        _debounce.Start();
    }

    // ---------- 記録 ----------

    private void Record()
    {
        try
        {
            if (DateTime.Now < _suspendUntil) return;
            var mons = DetectMonitors();
            if (mons.Count == 0) return;
            bool changed = EnterConfig(mons);
            var frozen = _frozen;
            string frozenId = _frozenId;
            _frozen = null;
            if (changed)
            {
                if (_started) AfterReconnect();
                Flush(force: true);
                return;
            }
            if (frozen is not null && frozenId == _currentId && AutoRestoreMode == 0)
            {
                // 構成は同じまま（スリープ復帰・一瞬の切断など）。Windows がウインドウを寄せていたら戻す
                var r = Restore(new LayoutSnapshot { Name = "切り替わり前", When = _frozenWhen, Windows = frozen });
                if (r.Moved > 0)
                {
                    Notify?.Invoke($"画面の切り替わりでずれた配置を戻しました：{r.Counts}");
                    return;
                }
            }
            if (AutoRecord)
            {
                var wins = Capture();
                // 取っている間に構成が変わっていたら捨てる
                if (wins.Count > 0 && MakeId(DetectMonitors()) == _currentId)
                {
                    var c = Current;
                    var now = DateTime.Now;
                    c.Live ??= new LayoutSnapshot { Started = now, When = now };
                    c.Live.Seen = now;
                    if (!c.Live.SameAs(wins))
                    {
                        c.Live.Windows = wins;
                        c.Live.When = now;
                        _dirty = true;
                    }
                }
            }
            Flush(force: false);
        }
        catch (Exception ex)
        {
            Logger.Log("ウインドウ配置の記録に失敗: " + ex.Message);
        }
    }

    /// <summary>いま開いているウインドウの配置を取る（手前から奥の順）</summary>
    public List<LayoutWindow> Capture()
    {
        var list = new List<LayoutWindow>();
        uint self = (uint)Environment.ProcessId;
        var alive = new HashSet<uint>();
        L.EnumWindows((h, _) =>
        {
            try
            {
                if (!L.IsWindowVisible(h)) return true;
                long ex = L.GetWindowLongPtr(h, L.GWL_EXSTYLE).ToInt64();
                if ((ex & L.WS_EX_TOOLWINDOW) != 0) return true;
                if (L.GetWindow(h, L.GW_OWNER) != IntPtr.Zero && (ex & L.WS_EX_APPWINDOW) == 0) return true;
                if (L.DwmGetWindowAttribute(h, L.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                string title = L.GetTitle(h);
                if (title.Length == 0) return true;
                string cls = SwNative.GetClassNameOf(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                L.GetWindowThreadProcessId(h, out uint pid);
                if (pid == self || pid == 0) return true;
                alive.Add(pid);

                var wp = new L.WINDOWPLACEMENT { length = Marshal.SizeOf<L.WINDOWPLACEMENT>() };
                if (!L.GetWindowPlacement(h, ref wp)) return true;
                if (!L.GetWindowRect(h, out var r)) return true;
                bool min = wp.showCmd == L.SW_SHOWMINIMIZED;
                bool max = wp.showCmd == L.SW_SHOWMAXIMIZED;
                if (!min && (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0)) return true;

                var proc = ProcOf(pid);
                list.Add(new LayoutWindow
                {
                    Hwnd = h.ToInt64(),
                    Exe = proc.Exe,
                    AppId = proc.AppId,
                    Elevated = proc.Elevated,
                    Class = cls,
                    Title = title,
                    State = min ? 2 : max ? 1 : 0,
                    RestoreToMax = min && (wp.flags & L.WPF_RESTORETOMAXIMIZED) != 0,
                    Normal = new[] { wp.rcNormalPosition.Left, wp.rcNormalPosition.Top, wp.rcNormalPosition.Right, wp.rcNormalPosition.Bottom },
                    Rect = new[] { r.Left, r.Top, r.Right, r.Bottom },
                    Z = list.Count,
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        foreach (var dead in _procCache.Keys.Where(k => !alive.Contains(k)).ToList()) _procCache.Remove(dead);
        return list;
    }

    private (string Exe, string AppId, bool Elevated) ProcOf(uint pid)
    {
        if (_procCache.TryGetValue(pid, out var cached)) return cached;
        string exe = "", appId = "";
        bool elevated = true; // 調べられないものは「起動しない」側に倒す
        IntPtr hp = L.OpenProcess(L.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (L.QueryFullProcessImageName(hp, 0, sb, ref size)) exe = sb.ToString();

                uint len = 256;
                var id = new StringBuilder((int)len);
                if (L.GetApplicationUserModelId(hp, ref len, id) == 0) appId = id.ToString();

                if (L.OpenProcessToken(hp, L.TOKEN_QUERY, out IntPtr tok))
                {
                    try
                    {
                        if (L.GetTokenInformation(tok, L.TokenElevation, out int isElevated, sizeof(int), out _)) elevated = isElevated != 0;
                    }
                    finally { L.CloseHandle(tok); }
                }
            }
            finally { L.CloseHandle(hp); }
        }
        var info = (exe, appId, elevated);
        _procCache[pid] = info;
        return info;
    }

    // ---------- セーブ ----------

    public LayoutSnapshot Save(string name)
    {
        var now = DateTime.Now;
        var snap = new LayoutSnapshot { Name = name, Started = now, When = now, Seen = now, Windows = Capture() };
        Current.Saves.Add(snap);
        _dirty = true;
        Flush(force: true);
        Logger.Log($"ウインドウ配置: セーブ「{name}」{snap.Windows.Count} 件（{Current.Description}）");
        return snap;
    }

    public void Overwrite(LayoutSnapshot snap)
    {
        snap.Windows = Capture();
        snap.When = snap.Seen = DateTime.Now;
        _dirty = true;
        Flush(force: true);
    }

    public void Rename(LayoutSnapshot snap, string name)
    {
        snap.Name = name;
        _dirty = true;
        Flush(force: true);
    }

    public void Delete(LayoutSnapshot snap)
    {
        if (Current.Saves.Remove(snap)) { _dirty = true; Flush(force: true); }
    }

    // ---------- 呼び出し ----------

    /// <param name="launchMissing">開いていないアプリを起動して配置する（自分で呼び出したときだけ使う）</param>
    public LayoutResult Restore(LayoutSnapshot snap, bool launchMissing = false)
    {
        var current = Capture();
        var used = new HashSet<long>();
        var pairs = new List<(LayoutWindow Saved, LayoutWindow Now)>();
        var rest = snap.Windows.ToList();

        void Match(Func<LayoutWindow, LayoutWindow, bool> rule)
        {
            foreach (var s in rest.ToList())
            {
                var c = current.FirstOrDefault(x => !used.Contains(x.Hwnd) && SameApp(s, x) && rule(s, x));
                if (c is null) continue;
                used.Add(c.Hwnd);
                pairs.Add((s, c));
                rest.Remove(s);
            }
        }
        Match((s, x) => s.Hwnd == x.Hwnd);       // 同じウインドウがまだ開いている
        Match((s, x) => s.Title == x.Title);     // 開き直したがタイトルが同じ
        Match((_, _) => true);                   // 同じアプリの残りを順番に

        int moved = 0, same = 0, failed = 0;
        IntPtr fg = SwNative.GetForegroundWindow();
        foreach (var (s, c) in pairs)
        {
            if (s.SamePlace(c)) { same++; continue; }
            if (Apply(s, new IntPtr(c.Hwnd))) moved++; else failed++;
        }

        // 重なりの順番：奥のものから順に最前面へ出していく（最小化は除く）
        foreach (var (s, c) in pairs.Where(p => p.Saved.State != 2).OrderByDescending(p => p.Saved.Z))
            L.SetWindowPos(new IntPtr(c.Hwnd), IntPtr.Zero, 0, 0, 0, 0, L.SWP_NOMOVE | L.SWP_NOSIZE | L.SWP_NOACTIVATE);
        if (fg != IntPtr.Zero && SwNative.IsWindow(fg) && !SwNative.IsIconic(fg)) SwNative.SetForegroundWindow(fg);

        int launching = launchMissing && rest.Count > 0 ? StartLaunch(rest, used) : 0;
        var result = new LayoutResult(moved, same, rest.Count - launching, failed, launching);
        Logger.Log($"ウインドウ配置: 呼び出し（{(snap.Name.Length > 0 ? snap.Name : "直前の配置")} {snap.When:MM/dd HH:mm}）→ {result.Message}");
        // 戻した直後のばたつきは記録しない
        _suspendUntil = DateTime.Now + TimeSpan.FromSeconds(2);
        _debounce.Stop();
        _debounce.Start();
        return result;
    }

    // ---------- 閉じていたアプリを起動して配置 ----------

    private DispatcherTimer? _launchTimer;

    private static bool CanLaunch(LayoutWindow s)
    {
        if (s.Elevated) return false;
        if (s.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return s.AppId.Length > 0 || (s.Exe.Length > 0 && File.Exists(s.Exe));
    }

    /// <summary>アプリごとに 1 回だけ起動し、ウインドウが出たら記録の位置へ並べる。起動した数を返す</summary>
    private int StartLaunch(List<LayoutWindow> missing, HashSet<long> used)
    {
        _launchTimer?.Stop();
        var pending = new List<LayoutWindow>();
        foreach (var g in missing.GroupBy(w => (w.AppId.Length > 0 ? w.AppId : w.Exe).ToLowerInvariant()))
        {
            var s = g.First();
            if (!CanLaunch(s)) { Logger.Log($"ウインドウ配置: 起動しない（管理者権限・対象外） {s.Exe}"); continue; }
            try
            {
                string target = s.AppId.Length > 0 ? "shell:AppsFolder\\" + s.AppId : s.Exe;
                Logger.Log("ウインドウ配置: 起動 " + target);
                // 管理者で常駐中でも、起動するアプリは一般権限に（エクスプローラー経由）
                if (Elevation.IsElevated) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                pending.Add(s);
            }
            catch (Exception ex) { Logger.Log($"ウインドウ配置: 起動に失敗 {s.Exe}: {ex.Message}"); }
        }
        if (pending.Count == 0) return 0;

        int started = pending.Count, placed = 0;
        var again = new List<(LayoutWindow Saved, IntPtr Hwnd, DateTime At)>();
        var deadline = DateTime.Now + TimeSpan.FromSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _launchTimer = timer;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (pending.Count > 0)
                {
                    var cur = Capture();
                    foreach (var p in pending.ToList())
                    {
                        var c = cur.FirstOrDefault(x => !used.Contains(x.Hwnd) && SameApp(p, x));
                        if (c is null) continue;
                        used.Add(c.Hwnd);
                        pending.Remove(p);
                        if (Apply(p, new IntPtr(c.Hwnd))) placed++;
                        again.Add((p, new IntPtr(c.Hwnd), DateTime.Now + TimeSpan.FromMilliseconds(1500)));
                    }
                }
                // 起動直後はアプリ自身が位置を直すことがあるので、少し置いてもう一度合わせる
                foreach (var a in again.Where(x => DateTime.Now >= x.At).ToList())
                {
                    again.Remove(a);
                    if (SwNative.IsWindow(a.Hwnd)) Apply(a.Saved, a.Hwnd);
                }
                _suspendUntil = DateTime.Now + TimeSpan.FromSeconds(2);
                bool timeout = DateTime.Now > deadline;
                if ((pending.Count == 0 && again.Count == 0) || (timeout && again.Count == 0))
                {
                    timer.Stop();
                    string msg = $"起動したアプリを配置しました：{placed} / {started}";
                    if (pending.Count > 0) msg += $"（ウインドウが出なかった {pending.Count}）";
                    Logger.Log("ウインドウ配置: " + msg);
                    Notify?.Invoke(msg);
                    _debounce.Stop();
                    _debounce.Start();
                }
            }
            catch (Exception ex)
            {
                timer.Stop();
                Logger.Log("ウインドウ配置: 起動後の配置に失敗: " + ex.Message);
            }
        };
        timer.Start();
        return started;
    }

    private static bool SameApp(LayoutWindow a, LayoutWindow b)
        => a.Class == b.Class && string.Equals(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase);

    private static bool Apply(LayoutWindow s, IntPtr h)
    {
        try
        {
            var wp = new L.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<L.WINDOWPLACEMENT>(),
                flags = s.RestoreToMax ? L.WPF_RESTORETOMAXIMIZED : 0,
                ptMinPosition = new L.POINT { X = -1, Y = -1 },
                ptMaxPosition = new L.POINT { X = -1, Y = -1 },
                rcNormalPosition = new L.RECT { Left = s.Normal[0], Top = s.Normal[1], Right = s.Normal[2], Bottom = s.Normal[3] },
            };
            if (s.State == 2)
            {
                wp.showCmd = L.SW_SHOWMINNOACTIVE;
                return L.SetWindowPlacement(h, ref wp);
            }
            // いったん通常の大きさで目的の位置（モニタ）へ置く
            wp.showCmd = L.SW_SHOWNOACTIVATE;
            if (!L.SetWindowPlacement(h, ref wp)) return false;
            if (s.State == 1)
            {
                L.ShowWindow(h, L.SW_SHOWMAXIMIZED);
                return true;
            }
            // 見えていた位置に合わせる（画面の端に吸着していたウインドウは通常時の位置と違う）。
            // 拡大率の違うモニタへ移るとアプリ側が大きさを変えることがあるので、ずれていたらもう一度
            int w = s.Rect[2] - s.Rect[0], hgt = s.Rect[3] - s.Rect[1];
            for (int i = 0; i < 2; i++)
            {
                L.SetWindowPos(h, IntPtr.Zero, s.Rect[0], s.Rect[1], w, hgt, L.SWP_NOZORDER | L.SWP_NOACTIVATE);
                if (L.GetWindowRect(h, out var r) && r.Left == s.Rect[0] && r.Top == s.Rect[1] && r.Right == s.Rect[2] && r.Bottom == s.Rect[3]) break;
            }
            return true;
        }
        catch { return false; }
    }

    // ---------- 保存 ----------

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _file = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(_path), JsonOpt) ?? new LayoutFile();
        }
        catch (Exception ex)
        {
            Logger.Log("layouts.json の読み込みに失敗: " + ex.Message);
            _file = new LayoutFile();
        }
    }

    private void Flush(bool force)
    {
        if (!_dirty) return;
        if (!force && DateTime.Now - _lastWrite < WriteInterval) return;
        try
        {
            string tmp = _path + ".tmp";
            // 全体を文字列にせず、直接ファイルへ書く（一時データを作らない）
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16384))
                JsonSerializer.Serialize(fs, _file, JsonOpt);
            File.Move(tmp, _path, overwrite: true);
            _dirty = false;
            _lastWrite = DateTime.Now;
        }
        catch (Exception ex)
        {
            Logger.Log("layouts.json の保存に失敗: " + ex.Message);
        }
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _debounce.Stop(); _periodic.Stop(); _settle.Stop(); _secondPass.Stop(); _launchTimer?.Stop();
        if (_hook != IntPtr.Zero) { L.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        Flush(force: true);
    }

    // ---------- Win32 ----------

    private static class L
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        public const uint GW_OWNER = 4;
        public const int DWMWA_CLOAKED = 14;
        public const int SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
        public const int WPF_RESTORETOMAXIMIZED = 2;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenElevation = 20;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B,
                          EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT ptMinPosition, ptMaxPosition;
            public RECT rcNormalPosition;
        }
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
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr hp, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetApplicationUserModelId(IntPtr hp, ref uint len, StringBuilder id);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr hp, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

        public static string GetTitle(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}

/// <summary>名前を1行入力してもらう小さなダイアログ</summary>
internal static class TextPrompt
{
    public static string? Show(string title, string message, string initial)
    {
        var win = new System.Windows.Window
        {
            Title = title, Width = 380, SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            ResizeMode = System.Windows.ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(14) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, Margin = new System.Windows.Thickness(0, 0, 0, 6), TextWrapping = System.Windows.TextWrapping.Wrap });
        var box = new System.Windows.Controls.TextBox { Text = initial, Padding = new System.Windows.Thickness(4) };
        panel.Children.Add(box);
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 12, 0, 0),
        };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 84, IsDefault = true, Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "キャンセル", Width = 84, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        win.Content = panel;
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); win.DialogResult = true; };
        win.Loaded += (_, _) => { win.Activate(); box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true && !string.IsNullOrEmpty(result) ? result : null;
    }
}
