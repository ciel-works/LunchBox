using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LunchBox;

/// <summary>
/// Office（Excel/Word/PowerPoint）の「最近使ったファイル」をレジストリから読む。
/// HKCU\Software\Microsoft\Office\16.0\&lt;App&gt;\User MRU\&lt;ID&gt;\File MRU
/// 値 "Item N" = "[F00000000][T01DC1E6B2A0D5B40][O00000000]*パス"（T は FILETIME の16進）
/// </summary>
internal static class OfficeMru
{
    private static readonly string[] Versions = { "16.0", "15.0" };

    public static string? RegistryAppName(HistorySource source) => source switch
    {
        HistorySource.Excel => "Excel",
        HistorySource.Word => "Word",
        HistorySource.PowerPoint => "PowerPoint",
        _ => null,
    };

    /// <summary>
    /// 「最近使ったファイル」が変わったかの目印（File MRU キーの最終書き込み日時と値の数）。
    /// 値を読まずに済むので、変わっていないときの定期収集を安くできる
    /// </summary>
    public static long Stamp(HistorySource source)
    {
        string? app = RegistryAppName(source);
        if (app is null) return 0;
        long s = 17;
        foreach (string version in Versions)
        {
            using var userMru = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{version}\{app}\User MRU");
            if (userMru is null) continue;
            foreach (string id in userMru.GetSubKeyNames())
            {
                using var fileMru = userMru.OpenSubKey(id + @"\File MRU");
                if (fileMru is null) continue;
                if (RegQueryInfoKey(fileMru.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        out uint values, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long lastWrite) != 0)
                    return 0; // 目印が取れない＝毎回読む
                s = Stamps.Mix(Stamps.Mix(s, lastWrite), values);
            }
        }
        return s;
    }

    public static List<RecentItem> Read(HistorySource source)
    {
        var result = new List<RecentItem>();
        string? app = RegistryAppName(source);
        if (app is null) return result;

        foreach (string version in Versions)
        {
            try
            {
                using var userMru = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{version}\{app}\User MRU");
                if (userMru is null) continue;
                foreach (string id in userMru.GetSubKeyNames())
                {
                    using var fileMru = userMru.OpenSubKey(id + @"\File MRU");
                    if (fileMru is null) continue;
                    foreach (string name in fileMru.GetValueNames())
                    {
                        if (!name.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)) continue;
                        if (fileMru.GetValue(name) is not string raw) continue;
                        if (!TryParseItem(raw, out long fileTime, out string path)) continue;
                        DateTime when;
                        try { when = DateTime.FromFileTime(fileTime); }
                        catch { when = DateTime.MinValue; }

                        var kind = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                   path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            ? ItemKind.Url : ItemKind.File;
                        result.Add(new RecentItem { Target = path, Kind = kind, When = when });
                    }
                }
                if (result.Count > 0) break; // 16.0 で取れたら 15.0 は見ない
            }
            catch (Exception ex)
            {
                Logger.Log($"Office MRU 読み取り失敗 ({app} {version}): {ex.Message}");
            }
        }

        // 同じファイルが複数の ID（アカウント）に出ることがあるので新しいほうを残す
        return result
            .GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.When).First())
            .OrderByDescending(r => r.When)
            .ToList();
    }

    /// <summary>
    /// "[F00000000][T01DC1E6B2A0D5B40][O00000000]*パス" を分解する。
    /// （正規表現を使わない＝常駐中に正規表現の部品を読み込まずに済む）
    /// </summary>
    internal static bool TryParseItem(string raw, out long fileTime, out string path)
    {
        fileTime = 0;
        path = "";
        var s = raw.AsSpan();
        if (!TakeHexField(ref s, 'F', out _)) return false;
        if (!TakeHexField(ref s, 'T', out var t)) return false;
        if (!TakeHexField(ref s, 'O', out _)) return false;
        if (s.Length < 2 || s[0] != '*') return false;
        path = s[1..].Trim().ToString();
        if (path.Length == 0) return false;
        // 日時が読めなくてもパスは使う（元の動作どおり、日時は「不明」になる）
        if (!long.TryParse(t, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out fileTime))
            fileTime = -1;
        return true;
    }

    /// <summary>先頭の "[X16進…]" を 1 つ取り出して、残りへ進める</summary>
    private static bool TakeHexField(ref ReadOnlySpan<char> s, char tag, out ReadOnlySpan<char> hex)
    {
        hex = default;
        if (s.Length < 4 || s[0] != '[' || s[1] != tag) return false;
        int end = s.IndexOf(']');
        if (end < 3) return false;
        hex = s[2..end];
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return false;
        s = s[(end + 1)..];
        return true;
    }

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, out uint lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);
}
