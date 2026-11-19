using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LunchBox;

// 年度カレンダー（依頼：docs\仕様書プロンプト_年度カレンダー_R8.11.13.md）
// このファイル：年度の範囲、祝日の計算、内閣府の祝日データの読み込みと更新、日付ごとの判定

/// <summary>年度（4 月始まり）の範囲</summary>
public static class FiscalCalendar
{
    /// <summary>表示する月数（当年度の 4 月〜翌々年度の 3 月）</summary>
    public const int Months = 36;

    public static int FiscalYearOf(DateOnly d) => d.Month >= 4 ? d.Year : d.Year - 1;

    public static DateOnly StartFor(DateOnly today) => new(FiscalYearOf(today), 4, 1);

    public static string FiscalYearName(int fy)
    {
        if (fy < 2019) return $"{fy}年度";
        int r = fy - 2018;
        return $"令和{(r == 1 ? "元" : r.ToString())}年度（{fy}年度）";
    }
}

/// <summary>祝日法のルールからの計算（現行の法律。公表データが無い期間の暫定表示と、公表データの検算に使う）</summary>
public static class HolidayRules
{
    /// <summary>検算に使える最初の年（2019〜2021 は即位・五輪の特例があるため対象外）</summary>
    public const int FirstCheckableYear = 2022;

    public static Dictionary<DateOnly, string> Compute(int year)
    {
        var h = new Dictionary<DateOnly, string>();
        void Add(int m, int d, string name) => h[new DateOnly(year, m, d)] = name;
        DateOnly NthMonday(int m, int n)
        {
            var d = new DateOnly(year, m, 1);
            while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }

        Add(1, 1, "元日");
        h[NthMonday(1, 2)] = "成人の日";
        Add(2, 11, "建国記念の日");
        Add(2, 23, "天皇誕生日");
        Add(3, Equinox(year, 20.8431), "春分の日");
        Add(4, 29, "昭和の日");
        Add(5, 3, "憲法記念日");
        Add(5, 4, "みどりの日");
        Add(5, 5, "こどもの日");
        h[NthMonday(7, 3)] = "海の日";
        Add(8, 11, "山の日");
        h[NthMonday(9, 3)] = "敬老の日";
        Add(9, Equinox(year, 23.2488), "秋分の日");
        h[NthMonday(10, 2)] = "スポーツの日";
        Add(11, 3, "文化の日");
        Add(11, 23, "勤労感謝の日");

        var named = new HashSet<DateOnly>(h.Keys);
        // 振替休日：祝日が日曜なら、その後の最初の「祝日でない日」
        foreach (var d in named.Where(x => x.DayOfWeek == DayOfWeek.Sunday).OrderBy(x => x))
        {
            var s = d.AddDays(1);
            while (named.Contains(s)) s = s.AddDays(1);
            h.TryAdd(s, "休日");
        }
        // 国民の休日：前日と翌日が祝日の平日
        foreach (var d in named.OrderBy(x => x))
        {
            var mid = d.AddDays(1);
            if (!h.ContainsKey(mid) && named.Contains(mid.AddDays(1)) && mid.DayOfWeek != DayOfWeek.Sunday) h[mid] = "休日";
        }
        return h;
    }

    /// <summary>春分・秋分の日（1980〜2099 年の計算式）</summary>
    private static int Equinox(int year, double baseDay)
        => (int)Math.Floor(baseDay + 0.242194 * (year - 1980) - Math.Floor((year - 1980) / 4.0));
}

public sealed class HolidayState
{
    /// <summary>取得に成功した年月（yyyy-MM）。月が変わったら取り直す</summary>
    public string LastSuccessMonth { get; set; } = "";
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string LastResult { get; set; } = "";
}

/// <summary>内閣府の祝日データ（公表データ）。同梱データ→保存済みデータの順に読み、月に 1 回だけ取りに行く</summary>
public sealed class HolidayService
{
    public const string SourceUrl = "https://www8.cao.go.jp/chosei/shukujitsu/syukujitsu.csv";
    private const int MinRows = 900;

    private sealed record Data(Dictionary<DateOnly, string> Map, DateOnly CoveredUntil, string Origin);

    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HttpClient Http = CreateHttp();

    private readonly string _dir;
    private volatile Data _data;
    private bool _checkedThisRun;
    private readonly Dictionary<int, Dictionary<DateOnly, string>> _rules = new();

    public HolidayState State { get; private set; } = new();
    public string CsvPath => Path.Combine(_dir, "syukujitsu.csv");
    private string StatePath => Path.Combine(_dir, "state.json");

    /// <summary>公表データがこの日まで収録されている（最後の祝日がある年の年末）</summary>
    public DateOnly CoveredUntil => _data.CoveredUntil;
    public int Rows => _data.Map.Count;
    public string Origin => _data.Origin;
    /// <summary>公表データの最後の祝日</summary>
    public DateOnly LastHoliday => _data.Map.Keys.Max();

    static HolidayService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static HttpClient CreateHttp()
    {
        // プロキシは Windows の既定設定に従う。こちらから送る情報は無い（取得だけ）
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LunchBox");
        return c;
    }

    public HolidayService(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _data = LoadInitial();
        try
        {
            if (File.Exists(StatePath)) State = JsonSerializer.Deserialize<HolidayState>(File.ReadAllText(StatePath), JsonOpt) ?? new();
        }
        catch { State = new(); }
    }

    private Data LoadInitial()
    {
        try
        {
            if (File.Exists(CsvPath))
            {
                var (map, err) = Parse(File.ReadAllBytes(CsvPath));
                if (map is not null && map.Count >= MinRows) return Make(map, "取得したデータ");
                Logger.Log("祝日データ: 保存済みのデータが読めないので同梱データを使います: " + err);
            }
        }
        catch (Exception ex) { Logger.Log("祝日データ: 保存済みデータの読み込みに失敗: " + ex.Message); }
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("syukujitsu.csv");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var (map, _) = Parse(ms.ToArray());
                if (map is not null) return Make(map, "同梱データ");
            }
        }
        catch (Exception ex) { Logger.Log("祝日データ: 同梱データの読み込みに失敗: " + ex.Message); }
        // どちらも読めないとき：すべて計算値で表示する
        return new Data(new Dictionary<DateOnly, string>(), new DateOnly(HolidayRules.FirstCheckableYear - 1, 12, 31), "なし（計算値のみ）");
    }

    private static Data Make(Dictionary<DateOnly, string> map, string origin)
        => new(map, new DateOnly(map.Keys.Max().Year, 12, 31), origin);

    /// <summary>CSV（Shift_JIS）を読む。読めなければ map=null と理由</summary>
    public static (Dictionary<DateOnly, string>? Map, string Error) Parse(byte[] bytes)
    {
        string text;
        try { text = Encoding.GetEncoding(932).GetString(bytes); }
        catch (Exception ex) { return (null, "文字コードを読めません: " + ex.Message); }
        var lines = text.Split('\n').Select(l => l.Trim('\r', ' ', '﻿')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].Contains("国民の祝日")) return (null, "見出し行がありません");
        var map = new Dictionary<DateOnly, string>();
        foreach (var line in lines.Skip(1))
        {
            int i = line.IndexOf(',');
            if (i <= 0) return (null, "形式が違う行があります: " + line);
            if (!DateOnly.TryParseExact(line[..i].Trim(), "yyyy/M/d", out var d)) return (null, "日付を読めない行があります: " + line);
            map[d] = line[(i + 1)..].Trim().Trim('"');
        }
        if (map.Count == 0) return (null, "データ行がありません");
        return (map, "");
    }

    /// <summary>取得したデータを確かめてから差し替える。おかしければ差し替えない</summary>
    public (bool Ok, string Message) TryReplace(byte[] bytes, DateTime now)
    {
        var (map, err) = Parse(bytes);
        string? reason = null;
        if (map is null) reason = err;
        else if (map.Count < Math.Max(MinRows, (int)(_data.Map.Count * 0.9))) reason = $"行数が少なすぎます（{map.Count} 行。いまは {_data.Map.Count} 行）";
        else if (!map.Keys.Any(d => d.Year == now.Year)) reason = $"{now.Year} 年の祝日が含まれていません";
        State.LastAttemptAt = now;
        if (reason is not null)
        {
            State.LastResult = "取得したデータに問題があったため、差し替えませんでした：" + reason;
            SaveState();
            Logger.Log("祝日データ: " + State.LastResult);
            return (false, State.LastResult);
        }
        try
        {
            string tmp = CsvPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, CsvPath, overwrite: true);
        }
        catch (Exception ex) { Logger.Log("祝日データ: 保存に失敗（メモリ上は差し替えます）: " + ex.Message); }
        _data = Make(map!, "取得したデータ");
        State.LastSuccessAt = now;
        State.LastSuccessMonth = now.ToString("yyyy-MM");
        State.LastResult = $"成功（{map!.Count} 件・{LastHoliday:yyyy年M月}まで収録）";
        SaveState();
        Logger.Log("祝日データ: 更新 " + State.LastResult);
        return (true, State.LastResult);
    }

    /// <summary>
    /// いま取りに行くべきか。月が変わって最初の起動、または常駐中に月が変わったときに 1 回。
    /// 失敗したら、次の起動時か翌日にもう一度。
    /// </summary>
    public bool ShouldUpdate(DateTime now)
    {
        if (State.LastSuccessMonth == now.ToString("yyyy-MM")) return false;
        if (!_checkedThisRun) return true;
        return State.LastAttemptAt is null || State.LastAttemptAt.Value.Date != now.Date;
    }

    /// <summary>取りに行く（通信はここだけ）。fetch はテスト用の差し替え口</summary>
    public async Task<(bool Ok, string Message)> UpdateAsync(DateTime now, Func<Task<byte[]>>? fetch = null)
    {
        _checkedThisRun = true;
        byte[] bytes;
        try
        {
            bytes = fetch is not null ? await fetch().ConfigureAwait(false) : await Http.GetByteArrayAsync(SourceUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            State.LastAttemptAt = now;
            State.LastResult = "取得できませんでした（前回のデータを使います）：" + ex.GetBaseException().Message;
            SaveState();
            Logger.Log("祝日データ: " + State.LastResult);
            return (false, State.LastResult);
        }
        return TryReplace(bytes, now);
    }

    private void SaveState()
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(State, JsonOpt)); }
        catch (Exception ex) { Logger.Log("祝日データ: 状態の保存に失敗: " + ex.Message); }
    }

    public Dictionary<DateOnly, string> RulesFor(int year)
    {
        lock (_rules)
        {
            if (!_rules.TryGetValue(year, out var r)) _rules[year] = r = HolidayRules.Compute(year);
            return r;
        }
    }

    /// <summary>公表データの祝日名。公表範囲の外、または祝日でなければ null</summary>
    public string? Official(DateOnly d) => _data.Map.TryGetValue(d, out var n) ? n : null;

    /// <summary>公表済みの年について、計算値と公表データを突き合わせる。食い違いの一覧を返す（公表データを優先）</summary>
    public List<string> CrossCheck()
    {
        var diffs = new List<string>();
        var data = _data;
        for (int y = HolidayRules.FirstCheckableYear; y <= data.CoveredUntil.Year; y++)
        {
            var rule = RulesFor(y);
            var official = data.Map.Where(kv => kv.Key.Year == y).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (official.Count == 0) continue;
            foreach (var kv in official)
                if (!rule.TryGetValue(kv.Key, out var n)) diffs.Add($"{kv.Key:yyyy/M/d} 公表={kv.Value} 計算=なし");
                else if (n != kv.Value) diffs.Add($"{kv.Key:yyyy/M/d} 公表={kv.Value} 計算={n}");
            foreach (var kv in rule)
                if (!official.ContainsKey(kv.Key)) diffs.Add($"{kv.Key:yyyy/M/d} 公表=なし 計算={kv.Value}");
        }
        return diffs;
    }

    public string StatusText()
    {
        var sb = new StringBuilder();
        sb.Append($"公表データの収録範囲：{LastHoliday:yyyy年M月}まで（{Rows} 件・{Origin}）");
        sb.Append($"\n最終更新：{(State.LastSuccessAt is null ? "まだ取得していません（同梱データを使用中）" : State.LastSuccessAt.Value.ToString("yyyy/MM/dd HH:mm"))}");
        if (State.LastAttemptAt is not null) sb.Append($"\n直近の取得（{State.LastAttemptAt:yyyy/MM/dd HH:mm}）：{State.LastResult}");
        return sb.ToString();
    }
}

/// <summary>利用者が登録した日付の一覧（会社休日・祝日の追加・祝日の取り消し）を、設定のテキストから読む</summary>
public static partial class HolidayText
{
    // 例：2026/12/29 年末年始休暇 ／ 2026-12-29,年末年始 ／ 2026/12/29～2027/1/3 年末年始休暇
    [GeneratedRegex(@"^\s*(\d{4})[/\-.年](\d{1,2})[/\-.月](\d{1,2})日?(?:\s*[~～〜\-－–]\s*(\d{4})[/\-.年](\d{1,2})[/\-.月](\d{1,2})日?)?[\s,、\t]*(.*)$")]
    private static partial Regex LineRegex();

    public static (Dictionary<DateOnly, string> Items, int BadLines) Parse(string? text)
    {
        var items = new Dictionary<DateOnly, string>();
        int bad = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '　', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = LineRegex().Match(line);
            if (!m.Success || !TryDate(m, 1, out var from)) { bad++; continue; }
            var to = from;
            if (m.Groups[4].Success && (!TryDate(m, 4, out to) || to < from || to.DayNumber - from.DayNumber > 366)) { bad++; continue; }
            string name = m.Groups[7].Value.Trim();
            for (var d = from; d <= to; d = d.AddDays(1)) items[d] = name;
        }
        return (items, bad);
    }

    private static bool TryDate(Match m, int g, out DateOnly d)
    {
        d = default;
        int y = int.Parse(m.Groups[g].Value), mo = int.Parse(m.Groups[g + 1].Value), da = int.Parse(m.Groups[g + 2].Value);
        if (y < 1950 || y > 2099 || mo < 1 || mo > 12 || da < 1 || da > DateTime.DaysInMonth(y, mo)) return false;
        d = new DateOnly(y, mo, da);
        return true;
    }
}

public enum DayKind { Normal, Holiday, ProvisionalHoliday }

public readonly record struct DayInfo(DayKind Kind, string HolidayName, bool Company, string CompanyName)
{
    public string Tip
    {
        get
        {
            string h = Kind switch
            {
                DayKind.Holiday => HolidayName,
                DayKind.ProvisionalHoliday => HolidayName + "（暫定）",
                _ => "",
            };
            string c = Company ? (CompanyName.Length > 0 ? CompanyName : "会社休日") : "";
            return h.Length > 0 && c.Length > 0 ? h + "／" + c : h + c;
        }
    }
}

/// <summary>1 回の表示で使う、日付ごとの判定（公表データ・計算値・利用者の登録を合わせる）</summary>
public sealed class CalendarData
{
    private readonly HolidayService _holidays;
    private readonly bool _showProvisional;
    private readonly Dictionary<DateOnly, string> _company, _adds, _removes;

    public DateOnly Today { get; }
    public DateOnly Start { get; }
    public bool WeekStartsMonday { get; }

    public CalendarData(HolidayService holidays, AppSettings s, DateOnly today)
    {
        _holidays = holidays;
        _showProvisional = s.CalendarShowProvisional;
        _company = HolidayText.Parse(s.CalendarCompanyHolidays).Items;
        _adds = HolidayText.Parse(s.CalendarHolidayAdds).Items;
        _removes = HolidayText.Parse(s.CalendarHolidayRemoves).Items;
        Today = today;
        Start = FiscalCalendar.StartFor(today);
        WeekStartsMonday = s.CalendarWeekStartsMonday;
    }

    public DayInfo Resolve(DateOnly d)
    {
        bool company = _company.TryGetValue(d, out var cname);
        var kind = DayKind.Normal;
        string name = "";
        if (_adds.TryGetValue(d, out var added))
        {
            kind = DayKind.Holiday;
            name = added.Length > 0 ? added : "祝日";
        }
        else if (!_removes.ContainsKey(d))
        {
            if (d <= _holidays.CoveredUntil)
            {
                var n = _holidays.Official(d);
                if (n is not null) { kind = DayKind.Holiday; name = n; }
            }
            else if (_showProvisional && _holidays.RulesFor(d.Year).TryGetValue(d, out var r))
            {
                kind = DayKind.ProvisionalHoliday;
                name = r;
            }
        }
        return new DayInfo(kind, name, company, cname ?? "");
    }
}
