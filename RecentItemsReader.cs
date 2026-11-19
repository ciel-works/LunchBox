using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LunchBox;

/// <summary>
/// Windows の「最近使った項目」（%AppData%\Microsoft\Windows\Recent\*.lnk）を読み、
/// 拡張子の関連付け（既定で開く exe）がそのアプリのものだけを履歴にする。
/// ジャンプリストに出ないアプリ（独自に履歴を持つアプリ等）の補完用。
/// </summary>
internal static class RecentItemsReader
{
    public static string RecentDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> AssocCache = new(StringComparer.OrdinalIgnoreCase);
    private static List<(string lnk, string target, DateTime when)>? _snapshot;
    private static DateTime _snapshotAt = DateTime.MinValue;

    /// <summary>「最近使った項目」が変わったかの目印（全 .lnk の更新日時）。読み直すかどうかの判定用</summary>
    public static long Stamp() => Stamps.OfFilesIn(RecentDirectory, "*.lnk");

    /// <summary>このアプリが既定で開く拡張子の項目だけを返す</summary>
    public static List<RecentItem> Read(AppEntry app, int maxLnk = 300)
    {
        var result = new List<RecentItem>();
        if (string.IsNullOrWhiteSpace(app.ExePath)) return result;
        string exe = NormalizeExe(app.ExePath);

        foreach (var (lnk, target, when) in Snapshot(maxLnk))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target)) continue;
                if (target.Length < 2 || (target[1] != ':' && !target.StartsWith(@"\\"))) continue;
                string ext = Path.GetExtension(target);
                if (string.IsNullOrEmpty(ext)) continue; // フォルダはエクスプローラー側で扱う
                string? handler = DefaultHandlerFor(ext);
                if (handler is null || !string.Equals(NormalizeExe(handler), exe, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new RecentItem { Target = target, Kind = ItemKind.File, When = when });
            }
            catch { }
        }
        return result
            .GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.When).First())
            .ToList();
    }

    /// <summary>.lnk の一覧とリンク先を読む（30秒キャッシュ。COM で1件ずつ解決するため）</summary>
    private static List<(string lnk, string target, DateTime when)> Snapshot(int maxLnk)
    {
        lock (Gate)
        {
            if (_snapshot is not null && DateTime.Now - _snapshotAt < TimeSpan.FromSeconds(30))
                return _snapshot;

            var list = new List<(string, string, DateTime)>();
            try
            {
                var files = new DirectoryInfo(RecentDirectory).GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(maxLnk)
                    .ToList();
                var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (_snapshot is not null)
                    foreach (var s in _snapshot) known[s.lnk] = s.target;

                // dynamic を使わずに名前で呼ぶ（ComLate の説明を参照）。解決が要るときだけ WScript.Shell を作る
                object? shell = null;
                bool shellTried = false;
                try
                {
                    foreach (var f in files)
                    {
                        string? target = null;
                        if (!known.TryGetValue(f.FullName, out target))
                        {
                            try
                            {
                                if (!shellTried) { shellTried = true; shell = ComLate.Create("WScript.Shell"); }
                                if (shell is not null)
                                {
                                    object? sc = ComLate.Call(shell, "CreateShortcut", f.FullName);
                                    try { target = sc is null ? null : ComLate.Get(sc, "TargetPath") as string; }
                                    finally { ComLate.Release(sc); }
                                }
                            }
                            catch { target = null; }
                        }
                        list.Add((f.FullName, target ?? "", f.LastWriteTime));
                    }
                }
                finally
                {
                    ComLate.Release(shell);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("最近使った項目の読み取り失敗: " + ex.Message);
            }
            _snapshot = list;
            _snapshotAt = DateTime.Now;
            return list;
        }
    }

    /// <summary>拡張子を既定で開く exe（無ければ null）</summary>
    public static string? DefaultHandlerFor(string ext)
    {
        lock (Gate)
        {
            if (AssocCache.TryGetValue(ext, out var cached)) return cached;
        }
        string? exe = null;
        try
        {
            uint len = 0;
            int hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, ext, null, null, ref len);
            if ((hr == 1 /*S_FALSE*/ || hr == 0) && len > 0)
            {
                var sb = new StringBuilder((int)len + 1);
                hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, ext, null, sb, ref len);
                if (hr == 0) exe = sb.ToString();
            }
        }
        catch { }
        lock (Gate) AssocCache[ext] = exe;
        return exe;
    }

    private static string NormalizeExe(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch { return path; }
    }

    private const uint ASSOCF_NONE = 0;
    private const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
}
