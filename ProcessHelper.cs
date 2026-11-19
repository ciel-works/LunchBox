using System.Diagnostics;

namespace LunchBox;

/// <summary>プロセス名の正規化と、プロセス名からの情報取得</summary>
internal static class ProcessHelper
{
    /// <summary>前後の空白と末尾の ".exe" を取り除く（Process.GetProcessesByName に渡せる形にする）</summary>
    public static string Normalize(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n;
    }

    /// <summary>
    /// 指定名のプロセスそれぞれから値を取り出す。アクセスできないプロセスは飛ばし、
    /// 取得した Process は使い終わったら破棄する（常駐アプリでハンドルを溜めないため）。
    /// </summary>
    public static List<T> SelectByName<T>(string name, Func<Process, T> selector)
    {
        var result = new List<T>();
        string n = Normalize(name);
        if (n.Length == 0) return result;

        foreach (var p in Process.GetProcessesByName(n))
        {
            using (p)
            {
                try { result.Add(selector(p)); }
                catch { /* 終了直後・権限不足などは無視 */ }
            }
        }
        return result;
    }

    /// <summary>指定名のプロセスのメインウインドウ一覧（ウインドウを持たないものは除く）</summary>
    public static List<IntPtr> MainWindowsByName(string name) =>
        SelectByName(name, p => p.MainWindowHandle).Where(h => h != IntPtr.Zero).ToList();
}
