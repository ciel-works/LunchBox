using System.IO;
using System.Text;
using System.Windows;
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;

namespace LunchBox;

/// <summary>ドラッグ＆ドロップされたデータを、登録候補（種類・パス/URL・ラベル）に変換する</summary>
internal static class DropParser
{
    public sealed record Dropped(ItemKind Kind, string Target, string Label);

    public static bool CanAccept(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent("UniformResourceLocatorW") ||
        data.GetDataPresent("UniformResourceLocator") ||
        data.GetDataPresent(DataFormats.Text);

    public static List<Dropped> Parse(IDataObject data)
    {
        var list = new List<Dropped>();
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (string f in files)
                {
                    // ブラウザから .url がドロップされた場合は中身の URL を使う
                    if (f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        string? url = ReadInternetShortcut(f);
                        if (url is not null)
                        {
                            list.Add(new Dropped(ItemKind.Url, url, Path.GetFileNameWithoutExtension(f)));
                            continue;
                        }
                    }
                    var kind = Directory.Exists(f) ? ItemKind.Folder : ItemKind.File;
                    list.Add(new Dropped(kind, f, ""));
                }
                if (list.Count > 0) return list;
            }

            string? dropped = GetUrl(data);
            if (dropped is not null)
            {
                string label = GetFileGroupTitle(data) ?? "";
                list.Add(new Dropped(ItemKind.Url, dropped, label));
                return list;
            }

            if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string text)
            {
                foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim().Trim('"');
                    if (line.Length == 0) continue;
                    var kind = PinItem.GuessKind(line);
                    if (kind == ItemKind.Url || PinItem.PathExists(line))
                        list.Add(new Dropped(kind, line, ""));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("ドロップ解析失敗: " + ex.Message);
        }
        return list;
    }

    private static string? GetUrl(IDataObject data)
    {
        foreach (string fmt in new[] { "UniformResourceLocatorW", "UniformResourceLocator" })
        {
            if (!data.GetDataPresent(fmt)) continue;
            object? o = null;
            try { o = data.GetData(fmt); } catch { }
            if (o is string s && s.Length > 0) return s.Trim('\0', ' ');
            if (o is MemoryStream ms)
            {
                byte[] b = ms.ToArray();
                string s2 = fmt.EndsWith("W") ? Encoding.Unicode.GetString(b) : Encoding.Default.GetString(b);
                s2 = s2.Trim('\0', ' ', '\r', '\n');
                if (s2.Length > 0) return s2;
            }
        }
        return null;
    }

    /// <summary>ブラウザからのドロップに含まれる "ページタイトル.url" からタイトルを取る</summary>
    private static string? GetFileGroupTitle(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("FileGroupDescriptorW") && data.GetData("FileGroupDescriptorW") is MemoryStream ms)
            {
                byte[] b = ms.ToArray();
                if (b.Length < 4 + 592) return null;
                // FILEDESCRIPTORW の cFileName はオフセット 72（先頭 4 バイトは件数）
                string name = Encoding.Unicode.GetString(b, 4 + 72, 520);
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name.Substring(0, nul);
                if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { }
        return null;
    }

    private static string? ReadInternetShortcut(string path)
    {
        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(4).Trim();
            }
        }
        catch { }
        return null;
    }
}
