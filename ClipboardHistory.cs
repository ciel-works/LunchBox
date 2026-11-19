using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace LunchBox;

public enum ClipKind { Text, Image }

/// <summary>クリップボード履歴の1件</summary>
public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    /// <summary>文字のとき本文</summary>
    public string? Text { get; set; }
    /// <summary>画像のとき PNG ファイル名（clips フォルダ内）</summary>
    public string? ImageFile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime When { get; set; }
    public bool Pinned { get; set; }
    /// <summary>重複判定用（文字＝本文の SHA1、画像＝PNG の SHA1）</summary>
    public string Hash { get; set; } = "";

    [JsonIgnore]
    public string Label
    {
        get
        {
            if (Kind == ClipKind.Image) return $"画像 {Width}×{Height}";
            string t = (Text ?? "").Trim();
            int nl = t.IndexOfAny(new[] { '\r', '\n' });
            string first = nl >= 0 ? t.Substring(0, nl) : t;
            first = CollapseWhitespace(first);
            if (first.Length == 0) first = "(空白)";
            int lines = t.Count(c => c == '\n') + 1;
            return lines > 1 ? $"{first}  (+{lines - 1}行)" : first;
        }
    }

    /// <summary>
    /// 連続する空白を 1 つの半角スペースにまとめ、前後の空白を取る。
    /// （正規表現を使わない＝メニューを組み立てるたびに正規表現の部品を動かさない）
    /// </summary>
    internal static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [JsonIgnore]
    public string ToolTip
    {
        get
        {
            string head = When.ToString("yyyy/MM/dd HH:mm");
            if (Kind == ClipKind.Image) return $"画像 {Width}×{Height} px{Environment.NewLine}{head}";
            string t = Text ?? "";
            if (t.Length > 400) t = t.Substring(0, 400) + " …";
            return t + Environment.NewLine + "―― " + head + $"（{(Text ?? "").Length} 文字）";
        }
    }
}

/// <summary>
/// クリップボード履歴の保存（%AppData%\LunchBox\clips.json ＋ clips\*.png）。
/// 収集スレッドは UI スレッドだが、保存は別スレッドから呼ばれることがあるので排他する。
/// </summary>
public sealed class ClipStore
{
    public List<ClipItem> Items { get; set; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Bitmap?> _thumbs = new();

    public static string StorePath => Path.Combine(AppSettings.SettingsDirectory, "clips.json");
    public static string ImageDirectory => Path.Combine(AppSettings.SettingsDirectory, "clips");
    public static string DefaultArchiveFolder => Path.Combine(AppSettings.SettingsDirectory, "clip_archive");

    /// <summary>最大件数を超えて捨てられる分をファイルへ書き出す（App が設定から反映する）</summary>
    [JsonIgnore] public bool ArchiveEnabled { get; set; }
    [JsonIgnore] public string ArchiveFolder { get; set; } = "";
    private string EffectiveArchiveFolder => string.IsNullOrWhiteSpace(ArchiveFolder) ? DefaultArchiveFolder : ArchiveFolder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ClipStore Load(bool persist)
    {
        var store = new ClipStore();
        try
        {
            if (!persist)
            {
                // 残さない設定：前回の保存物を捨てる
                try { if (File.Exists(StorePath)) File.Delete(StorePath); } catch { }
                try { if (Directory.Exists(ImageDirectory)) Directory.Delete(ImageDirectory, recursive: true); } catch { }
                return store;
            }
            if (File.Exists(StorePath))
            {
                using var fs = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384);
                var loaded = JsonSerializer.Deserialize<ClipStore>(fs, JsonOptions);
                if (loaded is not null) store = loaded;
            }
            // 画像ファイルが無いものは捨てる
            store.Items.RemoveAll(i => i.Kind == ClipKind.Image &&
                                       (string.IsNullOrEmpty(i.ImageFile) || !File.Exists(Path.Combine(ImageDirectory, i.ImageFile))));
        }
        catch (Exception ex)
        {
            Logger.Log("clips.json 読み込み失敗: " + ex.Message);
        }
        return store;
    }

    public void Save(bool persist)
    {
        if (!persist) return;
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            string tmp = StorePath + ".tmp";
            // 全体を文字列にせず、直接ファイルへ書く（コピーのたびに一時データを作らない。11/17）
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16384))
            {
                lock (_gate) JsonSerializer.Serialize(fs, this, JsonOptions);
            }
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Log("clips.json 保存失敗: " + ex.Message);
        }
    }

    public List<ClipItem> Snapshot()
    {
        lock (_gate) return Items.ToList();
    }

    public int Count(ClipKind kind)
    {
        lock (_gate) return Items.Count(i => i.Kind == kind);
    }

    /// <summary>文字を追加（同じ内容があれば先頭へ移動）。戻り値＝変化があったか</summary>
    public bool AddText(string text, int maxText)
    {
        string hash = Sha1(Encoding.UTF8.GetBytes(text));
        lock (_gate)
        {
            var existing = Items.FirstOrDefault(i => i.Kind == ClipKind.Text && i.Hash == hash);
            if (existing is not null)
            {
                Items.Remove(existing);
                existing.When = DateTime.Now;
                Items.Insert(0, existing);
                return true;
            }
            Items.Insert(0, new ClipItem { Kind = ClipKind.Text, Text = text, Hash = hash, When = DateTime.Now });
            TrimNoLock(ClipKind.Text, maxText);
            return true;
        }
    }

    /// <summary>画像を追加（PNG にして保存。同じ画像があれば先頭へ移動）</summary>
    public bool AddImage(Image image, int maxImages)
    {
        byte[] png;
        using (var ms = new MemoryStream())
        {
            using (var bmp = new Bitmap(image)) bmp.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }
        string hash = Sha1(png);
        lock (_gate)
        {
            var existing = Items.FirstOrDefault(i => i.Kind == ClipKind.Image && i.Hash == hash);
            if (existing is not null)
            {
                Items.Remove(existing);
                existing.When = DateTime.Now;
                Items.Insert(0, existing);
                return true;
            }
            Directory.CreateDirectory(ImageDirectory);
            string file = hash + ".png";
            File.WriteAllBytes(Path.Combine(ImageDirectory, file), png);
            Items.Insert(0, new ClipItem
            {
                Kind = ClipKind.Image, ImageFile = file, Width = image.Width, Height = image.Height, Hash = hash, When = DateTime.Now,
            });
            TrimNoLock(ClipKind.Image, maxImages);
            return true;
        }
    }

    public void SetPinned(ClipItem item, bool pinned)
    {
        lock (_gate) item.Pinned = pinned;
    }

    public void Remove(ClipItem item)
    {
        lock (_gate)
        {
            Items.Remove(item);
            DeleteImageIfUnusedNoLock(item);
        }
    }

    /// <summary>履歴を消す（kind が null なら全部）。ピン留めは残す。戻り値＝消した件数</summary>
    public int Clear(ClipKind? kind)
    {
        lock (_gate)
        {
            var targets = Items.Where(i => !i.Pinned && (kind is null || i.Kind == kind)).ToList();
            foreach (var t in targets)
            {
                Items.Remove(t);
                DeleteImageIfUnusedNoLock(t);
            }
            return targets.Count;
        }
    }

    /// <summary>上限を超えた分（ピン留め以外・古い順）を捨てる</summary>
    public void Trim(int maxText, int maxImages)
    {
        lock (_gate)
        {
            TrimNoLock(ClipKind.Text, maxText);
            TrimNoLock(ClipKind.Image, maxImages);
        }
    }

    private void TrimNoLock(ClipKind kind, int max)
    {
        if (max < 1) max = 1;
        var unpinned = Items.Where(i => i.Kind == kind && !i.Pinned).ToList(); // 新しい順
        // 古いものから順に書き出す（ファイル内で時系列になるように）
        for (int k = unpinned.Count - 1; k >= max; k--)
        {
            if (ArchiveEnabled) ArchiveNoLock(unpinned[k]);
            Items.Remove(unpinned[k]);
            DeleteImageIfUnusedNoLock(unpinned[k]);
        }
    }

    /// <summary>あふれた1件を書き出す。文字＝月ごとのテキストに追記、画像＝PNG のまま保存</summary>
    private void ArchiveNoLock(ClipItem item)
    {
        try
        {
            string folder = EffectiveArchiveFolder;
            Directory.CreateDirectory(folder);
            if (item.Kind == ClipKind.Text)
            {
                string path = Path.Combine(folder, $"テキスト_{item.When:yyyy-MM}.txt");
                string block = $"===== {item.When:yyyy/MM/dd HH:mm:ss} ====={Environment.NewLine}{item.Text}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(path, block, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            else if (!string.IsNullOrEmpty(item.ImageFile))
            {
                string dir = Path.Combine(folder, "画像", item.When.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                string name = $"{item.When:yyyyMMdd_HHmmss}_{item.Hash.Substring(0, Math.Min(8, item.Hash.Length))}.png";
                string src = Path.Combine(ImageDirectory, item.ImageFile);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dir, name), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("あふれた履歴の書き出し失敗: " + ex.Message);
        }
    }

    public string ArchiveFolderPath => EffectiveArchiveFolder;

    private void DeleteImageIfUnusedNoLock(ClipItem item)
    {
        if (item.Kind != ClipKind.Image || string.IsNullOrEmpty(item.ImageFile)) return;
        if (Items.Any(i => i.ImageFile == item.ImageFile)) return;
        try { File.Delete(Path.Combine(ImageDirectory, item.ImageFile)); } catch { }
        lock (_thumbs) _thumbs.Remove(item.Hash);
    }

    /// <summary>メニュー用の縮小画像（32px）。無ければ作ってキャッシュ</summary>
    public Bitmap? Thumbnail(ClipItem item)
    {
        if (item.Kind != ClipKind.Image || string.IsNullOrEmpty(item.ImageFile)) return null;
        lock (_thumbs)
        {
            if (_thumbs.TryGetValue(item.Hash, out var hit)) return hit;
        }
        Bitmap? thumb = null;
        try
        {
            string path = Path.Combine(ImageDirectory, item.ImageFile);
            using var img = Image.FromFile(path);
            const int size = 32;
            thumb = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            float scale = Math.Min((float)size / img.Width, (float)size / img.Height);
            int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
            g.DrawImage(img, (size - w) / 2, (size - h) / 2, w, h);
            using var pen = new Pen(Color.FromArgb(120, 0, 0, 0));
            g.DrawRectangle(pen, (size - w) / 2, (size - h) / 2, w - 1, h - 1);
        }
        catch (Exception ex)
        {
            Logger.Log("縮小画像の作成失敗: " + ex.Message);
        }
        lock (_thumbs) _thumbs[item.Hash] = thumb;
        return thumb;
    }

    public Image? LoadImage(ClipItem item)
    {
        if (item.Kind != ClipKind.Image || string.IsNullOrEmpty(item.ImageFile)) return null;
        try
        {
            // ファイルをロックしないようメモリへ読み込む
            byte[] bytes = File.ReadAllBytes(Path.Combine(ImageDirectory, item.ImageFile));
            return Image.FromStream(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            Logger.Log("画像の読み込み失敗: " + ex.Message);
            return null;
        }
    }

    public string ImagePath(ClipItem item) => Path.Combine(ImageDirectory, item.ImageFile ?? "");

    private static string Sha1(byte[] data)
    {
        var hash = SHA1.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// クリップボードの変化を監視して履歴に入れる（AddClipboardFormatListener → WM_CLIPBOARDUPDATE）。
/// UI スレッド（STA）で動かすこと。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const string FormatExclude = "ExcludeClipboardContentFromMonitorProcessing";
    private const string FormatCanInclude = "CanIncludeInClipboardHistory";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly HwndSource _source;
    private readonly Func<AppSettings> _getSettings;
    private readonly ClipStore _store;
    private bool _listening;
    private string? _ignoreHashOnce;

    /// <summary>履歴が変わったとき（保存に使う）</summary>
    public event Action? Changed;

    public ClipboardMonitor(Func<AppSettings> getSettings, ClipStore store)
    {
        _getSettings = getSettings;
        _store = store;
        var parameters = new HwndSourceParameters("LunchBoxClipboardWindow")
        {
            Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void Apply(AppSettings settings)
    {
        if (settings.ClipboardEnabled && !_listening)
        {
            _listening = AddClipboardFormatListener(_source.Handle);
            Logger.Log($"クリップボード監視 開始 -> {_listening}");
        }
        else if (!settings.ClipboardEnabled && _listening)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _listening = false;
            Logger.Log("クリップボード監視 停止");
        }
        _store.Trim(settings.ClipboardMaxText, settings.ClipboardMaxImages);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // コピー元がまだクリップボードを掴んでいることがあるので、少し後で読む
            _source.Dispatcher.BeginInvoke(new Action(Capture), System.Windows.Threading.DispatcherPriority.Background);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Capture()
    {
        var settings = _getSettings();
        if (!settings.ClipboardEnabled) return;
        try
        {
            var data = WinForms.Clipboard.GetDataObject();
            if (data is null) return;

            // パスワード管理ソフト等が「履歴に入れないで」と指定したもの（Windows 標準のクリップボード履歴と同じ約束）
            if (data.GetDataPresent(FormatExclude)) return;
            if (data.GetDataPresent(FormatCanInclude))
            {
                try
                {
                    if (data.GetData(FormatCanInclude) is MemoryStream ms && ms.Length >= 4)
                    {
                        var b = new byte[4];
                        ms.Position = 0;
                        ms.Read(b, 0, 4);
                        if (BitConverter.ToInt32(b, 0) == 0) return;
                    }
                }
                catch { }
            }

            bool changed = false;
            if (data.GetDataPresent(WinForms.DataFormats.UnicodeText))
            {
                string? text = null;
                try { text = data.GetData(WinForms.DataFormats.UnicodeText) as string; } catch { }
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 1_000_000)
                    changed = _store.AddText(text, settings.ClipboardMaxText);
            }
            else if (settings.ClipboardKeepImages && data.GetDataPresent(WinForms.DataFormats.Bitmap))
            {
                Image? img = null;
                try { img = WinForms.Clipboard.GetImage(); } catch { }
                if (img is not null)
                {
                    using (img)
                    {
                        long pixels = (long)img.Width * img.Height;
                        if (pixels > 0 && pixels <= 40_000_000) // 約 40 メガピクセルまで
                            changed = _store.AddImage(img, settings.ClipboardMaxImages);
                    }
                }
            }
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log("クリップボード読み取り失敗: " + ex.Message);
        }
    }

    /// <summary>履歴の項目をクリップボードへ戻す</summary>
    public bool CopyToClipboard(ClipItem item)
    {
        try
        {
            if (item.Kind == ClipKind.Text)
            {
                WinForms.Clipboard.SetText(item.Text ?? "");
                return true;
            }
            using var img = _store.LoadImage(item);
            if (img is null) return false;
            WinForms.Clipboard.SetImage(img);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log("クリップボードへの書き込み失敗: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_listening) RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
