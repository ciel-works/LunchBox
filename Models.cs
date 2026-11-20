using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunchBox;

/// <summary>ショートカットの種類</summary>
public enum ItemKind { File, Folder, Url }

/// <summary>「最近」の自動収集元。Windows ＝ ジャンプリスト＋最近使った項目（汎用アプリ向け）</summary>
public enum HistorySource { None, Excel, Word, PowerPoint, Explorer, Chrome, Windows }

/// <summary>リンクの状態</summary>
public enum LinkStatus { Unknown, Ok, Relinked, NotFound }

/// <summary>ピン留め1件</summary>
public sealed class PinItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>表示ラベル。空ならファイル名などから自動</summary>
    public string Label { get; set; } = "";
    public ItemKind Kind { get; set; }
    /// <summary>登録時の表記（Z:\... など）。表示にはこれを使う</summary>
    public string Target { get; set; } = "";
    /// <summary>UNC に正規化したパス（ローカル・URL は null）</summary>
    public string? TargetUnc { get; set; }
    public string? Arguments { get; set; }
    /// <summary>手動指定アイコン（ico/exe/png）。null なら自動</summary>
    public string? IconPath { get; set; }
    /// <summary>NTFS ファイル ID 追跡用</summary>
    public string? VolumeId { get; set; }
    public string? FileId { get; set; }
    public string? VolumeRoot { get; set; }
    public string? Memo { get; set; }
    public DateTime? LastChecked { get; set; }
    public LinkStatus Status { get; set; }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? DefaultLabel(Kind, Target) : Label;

    /// <summary>実際に開くときに使うパス。登録表記が存在すればそれ、無ければ UNC</summary>
    [JsonIgnore]
    public string EffectiveTarget
    {
        get
        {
            if (Kind == ItemKind.Url) return Target;
            if (PathExists(Target)) return Target;
            if (!string.IsNullOrEmpty(TargetUnc) && PathExists(TargetUnc)) return TargetUnc;
            return Target;
        }
    }

    public static bool PathExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    public static string DefaultLabel(ItemKind kind, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "(未設定)";
        try
        {
            switch (kind)
            {
                case ItemKind.Url:
                    if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
                        return uri.Host + (uri.AbsolutePath.Length > 1 ? uri.AbsolutePath : "");
                    return target;
                case ItemKind.Folder:
                {
                    string trimmed = target.TrimEnd('\\', '/');
                    string name = Path.GetFileName(trimmed);
                    return string.IsNullOrEmpty(name) ? trimmed : name;
                }
                default:
                    return Path.GetFileName(target);
            }
        }
        catch
        {
            return target;
        }
    }

    public static ItemKind GuessKind(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return ItemKind.File;
        string t = target.Trim();
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return ItemKind.Url;
        try
        {
            if (Directory.Exists(t)) return ItemKind.Folder;
            if (File.Exists(t)) return ItemKind.File;
        }
        catch { }
        // 存在しないときは拡張子の有無で判断
        return string.IsNullOrEmpty(Path.GetExtension(t)) ? ItemKind.Folder : ItemKind.File;
    }

    public PinItem Clone() => JsonSerializer.Deserialize<PinItem>(JsonSerializer.Serialize(this))!;
}

/// <summary>登録アプリ1件</summary>
public sealed class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    /// <summary>手動指定アイコン。null なら exe から</summary>
    public string? IconPath { get; set; }
    public HistorySource Source { get; set; } = HistorySource.None;
    /// <summary>ジャンプリストを探すときの AppUserModelID（任意。空なら既知の表と exe パスから計算）</summary>
    public string? AppUserModelId { get; set; }
    /// <summary>true ならサブメニュー（グループ）を作らず、メニューのクリックでそのまま起動する</summary>
    public bool DirectLaunch { get; set; }
    /// <summary>管理者として実行する（UAC の確認が出る。ストアアプリでは無効）</summary>
    public bool RunAsAdmin { get; set; }
    /// <summary>shell:AppsFolder\... で起動するもの（ストアアプリなど）か</summary>
    [JsonIgnore]
    public bool IsShellApp => ShellApps.IsShellApp(ExePath);

    /// <summary>サブメニューを出さずに直接起動する扱いか（設定で指定、またはピン留めも履歴取得元も無い）</summary>
    [JsonIgnore]
    public bool IsDirectLaunch => DirectLaunch || (Pins.Count == 0 && Source == HistorySource.None);
    public List<PinItem> Pins { get; set; } = new();

    public override string ToString() => Name;
}

/// <summary>「最近」の1件（段階2で収集。段階1では空）</summary>
public sealed class RecentItem
{
    public string Target { get; set; } = "";
    public string? Title { get; set; }
    public ItemKind Kind { get; set; }
    public DateTime When { get; set; }
}

/// <summary>アプリ設定（%AppData%\LunchBox\settings.json）</summary>
public sealed class AppSettings
{
    public const string AppName = "LunchBox";

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    public static string HistoryPath => Path.Combine(SettingsDirectory, "history.json");
    public static string LayoutsPath => Path.Combine(SettingsDirectory, "layouts.json");
    public static string HolidayDirectory => Path.Combine(SettingsDirectory, "holidays");
    public static string IconCacheDirectory => Path.Combine(SettingsDirectory, "icons");

    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>メインメニューのホットキー（既定 Alt+Shift+Y。11/19 までは Ctrl+無変換）</summary>
    // 既定は Alt+Shift+Y（11/19 変更。無変換は日本語キーボード専用で、入力モードが変わることもあるため既定にしない）
    public uint HotKeyModifiers { get; set; } = HotKeyService.MOD_ALT | HotKeyService.MOD_SHIFT;
    public uint HotKeyVirtualKey { get; set; } = 0x59; // Y
    public bool HotKeyEnabled { get; set; } = true;
    /// <summary>ホットキーで検索ウインドウを開く（false ならメニュー）</summary>
    public bool HotKeyOpensSearch { get; set; } = true;
    /// <summary>トレイアイコンの左右クリックを入れ替える（false＝左:検索／右:メニュー、true＝左:メニュー／右:検索）</summary>
    public bool SwapTrayClicks { get; set; } = false;

    // ---- ウインドウ切替（旧 FrontSwitcher を統合） ----
    public bool SwitcherEnabled { get; set; } = false;
    /// <summary>ウインドウ配置を自動で記録する（「直前の配置に戻す」用）</summary>
    // ---- 通知バナーの表示先 ----
    /// <summary>通知バナーを指定したモニタに出す（OFF＝Windows の位置のまま）</summary>
    public bool NotifyPlaceEnabled { get; set; } = false;
    /// <summary>表示先：0=指定したモニタ 1=マウスカーソルのあるモニタ 2=前面のウインドウがあるモニタ</summary>
    public int NotifyPlaceMode { get; set; } = 0;
    /// <summary>指定したモニタ（機種:幅x高さ@X,Y）</summary>
    public string NotifyPlaceMonitor { get; set; } = "";

    // ---- 年度カレンダー ----
    public bool CalendarEnabled { get; set; } = true;
    /// <summary>年度カレンダーのホットキー（Alt+無変換は IME のカタカナ入力に切り替わることが多いので 11/16 に既定から外し、Alt+Shift+Z を経て 11/19 に Win+Y）</summary>
    // 既定は Win+Y（11/19 変更）
    public uint CalendarHotKeyModifiers { get; set; } = HotKeyService.MOD_WIN;
    public uint CalendarHotKeyVirtualKey { get; set; } = 0x59;
    /// <summary>表示するモニタ：0=一番右 1=一番左 2=メイン 3=カーソルのあるモニタ</summary>
    public int CalendarMonitor { get; set; } = 0;
    public bool CalendarWeekStartsMonday { get; set; } = false;
    /// <summary>月が変わったら祝日データを自動で取り直す</summary>
    public bool CalendarHolidayAutoUpdate { get; set; } = true;
    /// <summary>LunchBox を起動するたびに祝日データを取り直す</summary>
    public bool CalendarHolidayUpdateOnStartup { get; set; } = true;
    /// <summary>公表されていない期間の祝日を計算値（暫定）で表示する</summary>
    public bool CalendarShowProvisional { get; set; } = true;
    /// <summary>会社休日（1 行に「日付 名称」。範囲は「2026/12/29～2027/1/3 名称」）</summary>
    public string CalendarCompanyHolidays { get; set; } = "";
    /// <summary>祝日の手動追加</summary>
    public string CalendarHolidayAdds { get; set; } = "";
    /// <summary>祝日の取り消し</summary>
    public string CalendarHolidayRemoves { get; set; } = "";

    public bool LayoutAutoRecord { get; set; } = true;
    /// <summary>つなぎ直したとき：0=自動で直前の配置に戻す 1=通知だけ 2=何もしない</summary>
    public int LayoutAutoRestoreMode { get; set; } = 0;
    /// <summary>呼び出したとき、閉じていたアプリを起動して配置する</summary>
    public bool LayoutLaunchMissing { get; set; } = false;
    /// <summary>ホットキー：直前の配置に戻す（VirtualKey=0 で割り当てなし）</summary>
    // 「直前の配置に戻す」の既定は Alt+Shift+Z（11/19 変更。それまでは割り当てなし）
    public uint LayoutRestoreHotKeyModifiers { get; set; } = HotKeyService.MOD_ALT | HotKeyService.MOD_SHIFT;
    public uint LayoutRestoreHotKeyVirtualKey { get; set; } = 0x5A;
    /// <summary>ホットキー：先頭のセーブを呼び出す（VirtualKey=0 で割り当てなし）</summary>
    public uint LayoutRecallHotKeyModifiers { get; set; } = 0;
    public uint LayoutRecallHotKeyVirtualKey { get; set; } = 0;
    public SwitcherSettings Switcher { get; set; } = new();
    /// <summary>FrontSwitcher の設定を引き継ぎ済みか</summary>
    public bool SwitcherMigrated { get; set; } = false;
    /// <summary>旧 FrontSwitcher の自動起動タスクを片付け済みか</summary>
    public bool FrontSwitcherTaskRemoved { get; set; } = false;
    /// <summary>管理者権限で常駐する（昇格して動くアプリのウインドウも隠せる）</summary>
    public bool RunElevated { get; set; } = false;

    /// <summary>「最近」の表示件数（1〜50。ピン留めと合わせて1つのサブメニューは最大 50 行）</summary>
    public int RecentCount { get; set; } = 10;
    /// <summary>1つのサブメニューに並べる最大行数（ピン留め＋最近）</summary>
    public const int MaxMenuRows = 50;
    /// <summary>アプリごとに保持する「最近」の件数（10〜20000）。全件は「すべて表示（検索）」で見る</summary>
    public int HistoryMaxPerApp { get; set; } = 500;
    /// <summary>メニューのラベル最大文字数</summary>
    public int MaxLabelLength { get; set; } = 25;
    public bool StartWithWindows { get; set; } = false;
    /// <summary>リンク確認の間隔（分）。0 = 起動時のみ</summary>
    public int LinkCheckIntervalMinutes { get; set; } = 0;
    /// <summary>Chrome の履歴プロファイル（段階2用）</summary>
    public string ChromeProfile { get; set; } = "Default";

    // ---- クリップボード履歴 ----
    public bool ClipboardEnabled { get; set; } = true;
    /// <summary>画像も保持する（既定 ON）</summary>
    public bool ClipboardKeepImages { get; set; } = true;
    public int ClipboardMaxText { get; set; } = 200;
    public int ClipboardMaxImages { get; set; } = 30;
    /// <summary>メニューに表示する件数</summary>
    public int ClipboardMenuCount { get; set; } = 15;
    /// <summary>再起動後も残す（ファイルに保存）。OFF なら終了で消える</summary>
    public bool ClipboardPersist { get; set; } = true;
    /// <summary>最大件数を超えて捨てられる分をファイルへ書き出す</summary>
    public bool ClipboardArchiveOverflow { get; set; } = false;
    /// <summary>書き出し先フォルダ（空なら %AppData%\LunchBox\clip_archive）</summary>
    public string ClipboardArchiveFolder { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (s is not null)
                {
                    s.Sanitize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("settings.json 読み込み失敗: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Log("settings.json 保存失敗: " + ex.Message);
        }
    }

    public void Sanitize()
    {
        if (RecentCount < 1) RecentCount = 1;
        if (RecentCount > MaxMenuRows) RecentCount = MaxMenuRows;
        if (HistoryMaxPerApp < 10) HistoryMaxPerApp = 10;
        if (HistoryMaxPerApp > 20000) HistoryMaxPerApp = 20000;
        if (MaxLabelLength < 10) MaxLabelLength = 10;
        if (MaxLabelLength > 80) MaxLabelLength = 80;
        if (LinkCheckIntervalMinutes < 0) LinkCheckIntervalMinutes = 0;
        Switcher ??= new();
        Switcher.Normalize();
        if (ClipboardMaxText < 1) ClipboardMaxText = 1;
        if (ClipboardMaxText > 20000) ClipboardMaxText = 20000;
        if (ClipboardMaxImages < 1) ClipboardMaxImages = 1;
        if (ClipboardMaxImages > 2000) ClipboardMaxImages = 2000;
        if (ClipboardMenuCount < 1) ClipboardMenuCount = 1;
        if (ClipboardMenuCount > MaxMenuRows) ClipboardMenuCount = MaxMenuRows;
        foreach (var a in Apps)
        {
            a.Pins ??= new();
            if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N");
        }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public AppEntry? FindApp(string id) => Apps.FirstOrDefault(a => a.Id == id);
}

/// <summary>
/// 「最近」の保存（history.json）。収集スレッドとUIスレッドの両方から触るので、メソッド内で排他する。
/// 「履歴から消す」した項目は Hidden に入れ、次の収集で復活しないようにする。
/// </summary>
public sealed class HistoryStore
{
    public Dictionary<string, List<RecentItem>> ByApp { get; set; } = new();
    public Dictionary<string, List<string>> Hidden { get; set; } = new();

    /// <summary>アプリごとの保持上限（App が設定から反映する）</summary>
    [JsonIgnore] public int MaxPerApp { get; set; } = 500;

    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static HistoryStore Load()
    {
        try
        {
            if (File.Exists(AppSettings.HistoryPath))
            {
                // ファイル全体を文字列にせず、読みながら組み立てる（起動時の一時データを減らす）
                using var fs = new FileStream(AppSettings.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384);
                return JsonSerializer.Deserialize<HistoryStore>(fs, JsonOptions) ?? new();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("history.json 読み込み失敗: " + ex.Message);
        }
        return new HistoryStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            string tmp = AppSettings.HistoryPath + ".tmp";
            // 全体を文字列にしてから書くと、保存のたびに数百 KB〜数 MB の一時データができる。直接ファイルへ書く（11/17）
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16384))
            {
                lock (_gate) JsonSerializer.Serialize(fs, this, JsonOptions);
            }
            File.Move(tmp, AppSettings.HistoryPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Log("history.json 保存失敗: " + ex.Message);
        }
    }

    public List<RecentItem> Get(string appId, int count, IEnumerable<string> excludeTargets)
    {
        lock (_gate)
        {
            if (!ByApp.TryGetValue(appId, out var list)) return new();
            var ex = new HashSet<string>(excludeTargets, StringComparer.OrdinalIgnoreCase);
            return list.Where(r => !ex.Contains(r.Target))
                       .OrderByDescending(r => r.When)
                       .Take(count)
                       .ToList();
        }
    }

    /// <summary>アプリの履歴の件数（件数だけ知りたいときに、並べ替えた複製を作らないため）</summary>
    public int Count(string appId)
    {
        lock (_gate)
            return ByApp.TryGetValue(appId, out var list) ? list.Count : 0;
    }

    /// <summary>追加または更新。同じ Target があれば新しい日時のほうを残す。戻り値＝変化があったか</summary>
    public bool Add(string appId, RecentItem item)
    {
        lock (_gate)
        {
            if (IsHiddenNoLock(appId, item.Target)) return false;
            if (!ByApp.TryGetValue(appId, out var list))
                ByApp[appId] = list = new();
            var existing = list.FirstOrDefault(r => string.Equals(r.Target, item.Target, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                bool changed = false;
                if (item.When > existing.When) { existing.When = item.When; changed = true; }
                if (!string.IsNullOrWhiteSpace(item.Title) && item.Title != existing.Title) { existing.Title = item.Title; changed = true; }
                return changed;
            }
            if (list.Count >= MaxPerApp)
            {
                // 上限いっぱいのとき、いちばん古い項目より古いものは入れない。
                // （入れてすぐ捨てることになり、毎回「変化あり」と数えられて保存が走るのを防ぐ）
                DateTime oldest = DateTime.MaxValue;
                foreach (var r in list) if (r.When < oldest) oldest = r.When;
                if (item.When <= oldest) return false;
            }
            list.Add(item);
            if (list.Count > MaxPerApp)
            {
                list.Sort((a, b) => a.When.CompareTo(b.When));
                list.RemoveRange(0, list.Count - MaxPerApp);
            }
            return true;
        }
    }

    /// <summary>複数まとめて追加。戻り値＝変化があった件数</summary>
    public int AddRange(string appId, IEnumerable<RecentItem> items)
    {
        int n = 0;
        foreach (var it in items) if (Add(appId, it)) n++;
        return n;
    }

    /// <summary>
    /// 収集元に合わせて置き換える：いま収集元にある項目だけを残し、無くなった項目は消す。
    /// （Chrome で履歴を消したら LunchBox からも消えるようにするため）戻り値＝変化があった件数
    /// </summary>
    public int Replace(string appId, IEnumerable<RecentItem> items)
    {
        lock (_gate)
        {
            var incoming = new Dictionary<string, RecentItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (string.IsNullOrWhiteSpace(it.Target)) continue;
                if (!incoming.TryGetValue(it.Target, out var cur) || it.When > cur.When)
                    incoming[it.Target] = it;
            }

            if (!ByApp.TryGetValue(appId, out var list))
                ByApp[appId] = list = new();

            int changed = list.RemoveAll(r => !incoming.ContainsKey(r.Target));
            // 件数が多くても遅くならないよう、既存分は辞書で引く
            var existingByTarget = new Dictionary<string, RecentItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in list) existingByTarget.TryAdd(r.Target, r);
            var hidden = Hidden.TryGetValue(appId, out var h) ? new HashSet<string>(h, StringComparer.OrdinalIgnoreCase) : null;
            foreach (var it in incoming.Values)
            {
                if (hidden is not null && hidden.Contains(it.Target)) continue;
                if (!existingByTarget.TryGetValue(it.Target, out var existing))
                {
                    list.Add(it);
                    existingByTarget[it.Target] = it;
                    changed++;
                    continue;
                }
                if (it.When > existing.When) { existing.When = it.When; changed++; }
                if (!string.IsNullOrWhiteSpace(it.Title) && it.Title != existing.Title) { existing.Title = it.Title; changed++; }
            }
            if (list.Count > MaxPerApp)
            {
                list.Sort((a, b) => b.When.CompareTo(a.When));
                list.RemoveRange(MaxPerApp, list.Count - MaxPerApp);
                changed++;
            }
            return changed;
        }
    }

    /// <summary>アプリの履歴を全部消す（非表示リストは残す）。戻り値＝消した件数</summary>
    public int Clear(string appId)
    {
        lock (_gate)
        {
            if (!ByApp.TryGetValue(appId, out var list)) return 0;
            int n = list.Count;
            list.Clear();
            return n;
        }
    }

    /// <summary>すべてのアプリの履歴を消す。戻り値＝消した件数</summary>
    public int ClearAll()
    {
        lock (_gate)
        {
            int n = ByApp.Values.Sum(l => l.Count);
            foreach (var l in ByApp.Values) l.Clear();
            return n;
        }
    }

    /// <summary>履歴から消す（以後の収集でも復活させない）</summary>
    public void Remove(string appId, string target)
    {
        lock (_gate)
        {
            if (ByApp.TryGetValue(appId, out var list))
                list.RemoveAll(r => string.Equals(r.Target, target, StringComparison.OrdinalIgnoreCase));
            if (!Hidden.TryGetValue(appId, out var hidden))
                Hidden[appId] = hidden = new();
            if (!hidden.Contains(target, StringComparer.OrdinalIgnoreCase))
                hidden.Add(target);
        }
    }

    private bool IsHiddenNoLock(string appId, string target) =>
        Hidden.TryGetValue(appId, out var h) && h.Contains(target, StringComparer.OrdinalIgnoreCase);
}
