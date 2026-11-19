using System.Windows.Threading;

namespace LunchBox;

/// <summary>
/// エクスプローラーで開いているフォルダを定期的に見て、新しく開いたフォルダを履歴に足す。
/// Shell.Application（COM）を使うので UI スレッドの DispatcherTimer で動かす。
/// </summary>
internal sealed class ExplorerWatcher : IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly HistoryStore _history;
    private readonly DispatcherTimer _timer;
    private HashSet<string> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private bool _first = true;

    public event Action? Changed;

    public ExplorerWatcher(Func<AppSettings> getSettings, HistoryStore history)
    {
        _getSettings = getSettings;
        _history = history;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _timer.Start();
        Poll();
    }

    /// <summary>今開いているフォルダを調べて履歴に反映する</summary>
    public void Poll()
    {
        var apps = _getSettings().Apps.Where(a => a.Source == HistorySource.Explorer).ToList();
        if (apps.Count == 0) return;

        List<string> open;
        try { open = OpenFolders(); }
        catch (Exception ex)
        {
            Logger.Log("エクスプローラーの列挙失敗: " + ex.Message);
            return;
        }

        var now = DateTime.Now;
        int changed = 0;
        var current = new HashSet<string>(open, StringComparer.OrdinalIgnoreCase);
        foreach (string path in current)
        {
            // 初回は開いている全部、以後は新しく開いたものだけ日時を更新
            if (!_first && _lastSeen.Contains(path)) continue;
            foreach (var app in apps)
            {
                if (_history.Add(app.Id, new RecentItem { Target = path, Kind = ItemKind.Folder, When = now }))
                    changed++;
            }
        }
        _lastSeen = current;
        _first = false;
        if (changed > 0)
        {
            Logger.Log($"エクスプローラー履歴 {changed} 件更新");
            Changed?.Invoke();
        }
    }

    /// <summary>開いているエクスプローラーのフォルダパス一覧</summary>
    private static List<string> OpenFolders()
    {
        var result = new List<string>();
        // dynamic を使わずに名前で呼ぶ（ComLate の説明を参照）
        object? shell = ComLate.Create("Shell.Application");
        if (shell is null) return result;
        object? windows = null;
        try
        {
            windows = ComLate.Get(shell, "Windows");
            if (windows is null) return result;
            int count = Convert.ToInt32(ComLate.Get(windows, "Count"));
            for (int i = 0; i < count; i++)
            {
                object? w = null;
                try
                {
                    w = ComLate.Call(windows, "Item", i);
                    if (w is null) continue;
                    string full = ComLate.GetString(w, "FullName");
                    if (!full.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string url = ComLate.GetString(w, "LocationURL");
                    string? path = UrlToPath(url);
                    // 特殊フォルダなどは Document.Folder.Self.Path を試す
                    path ??= SelfPath(w);
                    if (string.IsNullOrWhiteSpace(path) || path.StartsWith("::")) continue;
                    if (!System.IO.Directory.Exists(path)) continue;
                    result.Add(path.TrimEnd('\\'));
                }
                catch { }
                finally
                {
                    ComLate.Release(w);
                }
            }
        }
        finally
        {
            ComLate.Release(windows);
            ComLate.Release(shell);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>エクスプローラーのウインドウから Document.Folder.Self.Path をたどる（取れなければ null）</summary>
    private static string? SelfPath(object window)
    {
        object? doc = null, folder = null, self = null;
        try
        {
            doc = ComLate.Get(window, "Document");
            if (doc is null) return null;
            folder = ComLate.Get(doc, "Folder");
            if (folder is null) return null;
            self = ComLate.Get(folder, "Self");
            return self is null ? null : ComLate.Get(self, "Path") as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            ComLate.Release(self);
            ComLate.Release(folder);
            ComLate.Release(doc);
        }
    }

    /// <summary>file:///Z:/a/b → Z:\a\b、file://server/share/a → \\server\share\a</summary>
    private static string? UrlToPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var u = new Uri(url);
            return u.LocalPath;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _timer.Stop();
}
