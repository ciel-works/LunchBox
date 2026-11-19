using System.Runtime.InteropServices;

namespace LunchBox;

/// <summary>
/// 「最近」の自動収集をまとめる。Office MRU と Chrome 履歴はバックグラウンドで、
/// エクスプローラーは ExplorerWatcher（UI スレッド）で集める。
/// </summary>
internal sealed class HistoryCollector
{
    private readonly Func<AppSettings> _getSettings;
    private readonly HistoryStore _history;
    private int _running;
    private DateTime _lastRun = DateTime.MinValue;

    // 収集元ごとの「変化の目印」（ファイルやレジストリの更新日時）。前回と同じなら読み直さない。
    // 1 分ごとの定期収集が、何も変わっていないのに毎回 数 MB の一時データを作っていたため（11/17）
    private readonly Dictionary<string, long> _stamps = new();
    private volatile bool _forceNext = true;
    private int _sinceFull;
    /// <summary>目印が同じでも、この回数に 1 回は全部読み直す（目印に出ない変化の取りこぼし対策）</summary>
    private const int FullEvery = 10;

    /// <summary>収集で履歴が変わったとき（保存などに使う）。スレッドプールから呼ばれる</summary>
    public event Action? Changed;

    public HistoryCollector(Func<AppSettings> getSettings, HistoryStore history)
    {
        _getSettings = getSettings;
        _history = history;
    }

    public bool IsStale(TimeSpan age) => DateTime.Now - _lastRun > age;

    /// <summary>次の収集では、目印が同じでも全部読み直す（設定の変更・履歴のクリアの後など）</summary>
    public void Invalidate() => _forceNext = true;

    /// <summary>バックグラウンドで収集を始める（実行中なら何もしない）</summary>
    /// <param name="force">true＝目印が同じでも全部読み直す（実行中だった場合は次の回に持ち越す）</param>
    public void CollectInBackground(bool force = false)
    {
        if (force) _forceNext = true;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { CollectNow(); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    /// <summary>同期で収集する（テスト・起動時用）</summary>
    public void CollectNow()
    {
        var settings = _getSettings();
        bool full = _forceNext || ++_sinceFull >= FullEvery;
        _forceNext = false;
        if (full) _sinceFull = 0;

        int changed = 0;
        foreach (var app in settings.Apps.ToList())
        {
            try
            {
                long stamp = SourceStamp(app, settings);
                if (!full && stamp != 0 && _stamps.TryGetValue(app.Id, out long last) && last == stamp) continue;

                bool read = true;
                switch (app.Source)
                {
                    // Office／Chrome／汎用は「収集元に合わせて置き換え」＝収集元から消えた項目は LunchBox からも消える
                    case HistorySource.Excel:
                    case HistorySource.Word:
                    case HistorySource.PowerPoint:
                        changed += _history.Replace(app.Id, OfficeMru.Read(app.Source));
                        break;
                    case HistorySource.Chrome:
                    {
                        // 読めなかったとき（null）は置き換えない＝既存の履歴を消さない
                        var chrome = ChromeData.ReadHistory(settings.ChromeProfile, Math.Clamp(settings.HistoryMaxPerApp, 30, 5000));
                        if (chrome is not null) changed += _history.Replace(app.Id, chrome);
                        else read = false;
                        break;
                    }
                    case HistorySource.Windows:
                        changed += _history.Replace(app.Id, JumpListReader.Read(app).Concat(RecentItemsReader.Read(app)));
                        break;
                    case HistorySource.Explorer:
                        // 開いているフォルダは ExplorerWatcher が担当（LunchBox 自身の記録なので溜める）。過去に開いたフォルダはジャンプリストから補う
                        changed += _history.AddRange(app.Id, JumpListReader.Read(app, ItemKind.Folder));
                        break;
                    default:
                        continue;
                }
                // 読めたときだけ目印を覚える（読めなかった回は、次も読みに行く）
                if (read && stamp != 0) _stamps[app.Id] = stamp;
                else _stamps.Remove(app.Id);
            }
            catch (Exception ex)
            {
                _stamps.Remove(app.Id);
                Logger.Log($"履歴収集エラー ({app.Name}): {ex.Message}");
            }
        }
        _lastRun = DateTime.Now;
        if (changed > 0)
        {
            Logger.Log($"履歴収集: {changed} 件更新");
            Changed?.Invoke();
        }
    }

    /// <summary>収集元が変わったかを安く見分ける目印。0＝目印なし（毎回読む）</summary>
    private static long SourceStamp(AppEntry app, AppSettings settings)
    {
        try
        {
            return app.Source switch
            {
                HistorySource.Excel or HistorySource.Word or HistorySource.PowerPoint => OfficeMru.Stamp(app.Source),
                HistorySource.Chrome => ChromeData.HistoryStamp(settings.ChromeProfile),
                HistorySource.Windows => RecentItemsReader.Stamp() is var recent && recent != 0 ? Stamps.Mix(JumpListReader.Stamp(app), recent) : 0,
                HistorySource.Explorer => JumpListReader.Stamp(app),
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>「変化の目印」の計算（更新日時と大きさを 1 つの数にまとめる）</summary>
internal static class Stamps
{
    public static long Mix(long a, long b) => unchecked(a * 1000003 + b);

    /// <summary>ファイルの更新日時と大きさ。無ければ 1</summary>
    public static long OfFile(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            return fi.Exists ? Mix(fi.LastWriteTimeUtc.Ticks, fi.Length) : 1;
        }
        catch { return 1; }
    }

    /// <summary>
    /// フォルダ内の、パターンに合うファイルすべての更新日時をまとめた目印。
    /// ファイルが増減しても、既にあるファイルが書き直されても変わる。無ければ 1。
    /// FindFirstFile を直接使い、ファイルごとの管理メモリを作らない（毎分呼ぶため）
    /// </summary>
    public static long OfFilesIn(string directory, string pattern)
    {
        IntPtr data = Marshal.AllocHGlobal(FindDataSize);
        IntPtr h = InvalidHandle;
        try
        {
            h = FindFirstFileW(System.IO.Path.Combine(directory, pattern), data);
            if (h == InvalidHandle) return 1;
            long s = 17;
            // WIN32_FIND_DATAW：ftLastWriteTime は先頭から 20 バイト目（x86・x64 共通）
            do { s = Mix(s, Marshal.ReadInt64(data, 20)); } while (FindNextFileW(h, data));
            return s;
        }
        catch { return 0; }
        finally
        {
            if (h != InvalidHandle) FindClose(h);
            Marshal.FreeHGlobal(data);
        }
    }

    private const int FindDataSize = 600; // WIN32_FIND_DATAW は 592 バイト
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, IntPtr lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, IntPtr lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
