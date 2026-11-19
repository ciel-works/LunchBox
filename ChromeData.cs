using System.IO;
using Microsoft.Data.Sqlite;

namespace LunchBox;

/// <summary>
/// Chrome のプロファイルから、閲覧履歴（History）とファビコン（Favicons）を読む。
/// どちらも Chrome 起動中はロックされているので、コピーしてから開く。
/// </summary>
internal static class ChromeData
{
    private static readonly object Gate = new();
    private static DateTime _faviconsCopiedAt = DateTime.MinValue;

    public static string ProfileDirectory(string profile) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data", string.IsNullOrWhiteSpace(profile) ? "Default" : profile);

    private static string CacheDirectory => Path.Combine(AppSettings.SettingsDirectory, "cache");

    /// <summary>元ファイルをキャッシュへコピーして、そのパスを返す（Chrome のロック回避）</summary>
    private static string? CopyToCache(string profile, string fileName, string cacheName, TimeSpan maxAge)
    {
        string src = Path.Combine(ProfileDirectory(profile), fileName);
        if (!File.Exists(src)) return null;
        Directory.CreateDirectory(CacheDirectory);
        string dst = Path.Combine(CacheDirectory, cacheName);
        try
        {
            if (File.Exists(dst) && DateTime.Now - File.GetLastWriteTime(dst) < maxAge)
                return dst;
            // Chrome が開いていても FileShare.ReadWrite で読める
            using (var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                s.CopyTo(d);
            // WAL があれば一緒にコピー（直近の書き込みが反映される）
            string wal = src + "-wal";
            if (File.Exists(wal))
            {
                using var ws = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var wd = new FileStream(dst + "-wal", FileMode.Create, FileAccess.Write, FileShare.None);
                ws.CopyTo(wd);
            }
            else
            {
                File.Delete(dst + "-wal");
            }
            return dst;
        }
        catch (Exception ex)
        {
            Logger.Log($"Chrome {fileName} のコピー失敗: {ex.Message}");
            return File.Exists(dst) ? dst : null;
        }
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite, // WAL を読むには書き込み可で開く必要がある（コピーなので安全）
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>閲覧履歴が変わったかの目印（History と History-wal の更新日時・大きさ）。読み直すかどうかの判定用</summary>
    public static long HistoryStamp(string profile)
    {
        string src = Path.Combine(ProfileDirectory(profile), "History");
        return Stamps.Mix(Stamps.OfFile(src), Stamps.OfFile(src + "-wal"));
    }

    /// <summary>閲覧履歴を新しい順に読む。読めなかったときは null（空リスト＝本当に0件、と区別する）</summary>
    public static List<RecentItem>? ReadHistory(string profile, int max)
    {
        var result = new List<RecentItem>();
        string? db;
        lock (Gate) db = CopyToCache(profile, "History", "chrome_history.db", TimeSpan.FromSeconds(20));
        if (db is null) return null;
        try
        {
            using var conn = Open(db);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT url, title, last_visit_time FROM urls
                                WHERE hidden = 0 AND last_visit_time > 0
                                ORDER BY last_visit_time DESC LIMIT @n";
            cmd.Parameters.AddWithValue("@n", max * 3);
            using var r = cmd.ExecuteReader();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read() && result.Count < max)
            {
                string url = r.IsDBNull(0) ? "" : r.GetString(0);
                string title = r.IsDBNull(1) ? "" : r.GetString(1);
                long t = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                if (!IsUsefulUrl(url)) continue;
                string key = NormalizeUrl(url);
                if (!seen.Add(key)) continue;
                result.Add(new RecentItem
                {
                    Target = url,
                    Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                    Kind = ItemKind.Url,
                    When = ChromeTime(t),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Chrome 履歴の読み取り失敗: " + ex.Message);
            return null;
        }
        return result;
    }

    /// <summary>ファビコンの PNG を返す（無ければ null）</summary>
    public static byte[]? ReadFavicon(string profile, string host)
    {
        string? db;
        lock (Gate) db = CopyToCache(profile, "Favicons", "chrome_favicons.db", TimeSpan.FromMinutes(10));
        if (db is null) return null;
        try
        {
            using var conn = Open(db);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT b.image_data, b.width FROM icon_mapping m
                                JOIN favicon_bitmaps b ON b.icon_id = m.icon_id
                                WHERE m.page_url LIKE @h1 OR m.page_url LIKE @h2 OR m.page_url LIKE @h3 OR m.page_url LIKE @h4
                                ORDER BY CASE WHEN b.width BETWEEN 16 AND 64 THEN 0 ELSE 1 END, b.width DESC
                                LIMIT 1";
            cmd.Parameters.AddWithValue("@h1", $"https://{host}/%");
            cmd.Parameters.AddWithValue("@h2", $"http://{host}/%");
            cmd.Parameters.AddWithValue("@h3", $"https://www.{host}/%");
            cmd.Parameters.AddWithValue("@h4", $"http://www.{host}/%");
            using var r = cmd.ExecuteReader();
            if (r.Read() && !r.IsDBNull(0))
            {
                var data = (byte[])r[0];
                return data.Length > 16 ? data : null;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Chrome ファビコンの読み取り失敗 ({host}): {ex.Message}");
        }
        return null;
    }

    private static bool IsUsefulUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        // 検索結果・認証系は履歴として意味が薄いので除く
        string host = u.Host.ToLowerInvariant();
        if (host.EndsWith("google.com") && (u.AbsolutePath.StartsWith("/search") || u.AbsolutePath.StartsWith("/url"))) return false;
        if (host.StartsWith("accounts.") || host.StartsWith("login.") || host.Contains("auth")) return false;
        if (url.Length > 1000) return false;
        return true;
    }

    private static string NormalizeUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        catch { return url; }
    }

    /// <summary>Chrome の時刻（1601-01-01 からのマイクロ秒）を DateTime に</summary>
    public static DateTime ChromeTime(long microseconds)
    {
        try { return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(microseconds * 10).ToLocalTime(); }
        catch { return DateTime.MinValue; }
    }
}
