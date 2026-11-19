using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;

namespace LunchBox;

/// <summary>
/// アプリ・ファイル・フォルダ・URL のアイコンを取得してメモリに保持する。
/// ・ファイルは拡張子の関連付けアイコン（ネットワーク上の実体には触れない）
/// ・URL はファビコン（%AppData%\LunchBox\icons にキャッシュ。無ければバックグラウンドで取得）
/// </summary>
internal static class IconCache
{
    private const int Size = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap?> Mem = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PendingFavicons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>ファビコンの取得が終わったとき（次回メニュー表示で反映）</summary>
    public static event Action? FaviconArrived;

    /// <summary>Chrome のファビコン DB を先に見るときのプロファイル名（設定から渡す）</summary>
    public static string ChromeProfile { get; set; } = "Default";

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LunchBox/1.0");
        return h;
    }

    // ---------- 公開 API ----------

    public static Bitmap? ForApp(AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.IconPath))
            return FromIconFile(app.IconPath) ?? ForExe(app.ExePath);
        return ForExe(app.ExePath);
    }

    public static Bitmap? ForPin(PinItem pin)
    {
        if (!string.IsNullOrWhiteSpace(pin.IconPath))
        {
            var custom = FromIconFile(pin.IconPath);
            if (custom is not null) return custom;
        }
        // 注意：EffectiveTarget はネットワークに触れる（存在確認）ので、ここでは登録表記だけで決める
        return ForTarget(pin.Kind, pin.Target);
    }

    public static Bitmap? ForTarget(ItemKind kind, string target)
    {
        switch (kind)
        {
            case ItemKind.Url:
                return Favicon(target) ?? BrowserIcon();
            case ItemKind.Folder:
                return FolderIcon();
            default:
                return FileIcon(target);
        }
    }

    public static Bitmap? ForExe(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (ShellApps.IsShellApp(exePath))
            return Cached("exe:" + exePath, () => ShellApps.GetIcon(exePath, Size));
        return Cached("exe:" + exePath, () =>
            File.Exists(exePath) ? ShellIcon(exePath, useAttributes: false, directory: false) : null);
    }

    /// <summary>ico / png / jpg / bmp / exe / dll から取得</summary>
    public static Bitmap? FromIconFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Cached("file:" + path, () =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                switch (ext)
                {
                    case ".ico":
                        using (var ico = new Icon(path, Size, Size))
                            return ico.ToBitmap();
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".bmp":
                    case ".gif":
                        using (var img = Image.FromFile(path))
                            return Resize(img, Size);
                    case ".exe":
                    case ".dll":
                    case ".lnk":
                        return ShellIcon(path, useAttributes: false, directory: false);
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"アイコン読み込み失敗 {path}: {ex.Message}");
                return null;
            }
        });
    }

    public static Bitmap? FileIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string ext = "";
        try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }

        // exe/lnk/ico はローカルに実体があれば実体から
        if ((ext == ".exe" || ext == ".lnk" || ext == ".ico") && IsLocal(path) && File.Exists(path))
            return Cached("file:" + path, () => ShellIcon(path, useAttributes: false, directory: false));

        if (string.IsNullOrEmpty(ext))
            return Cached("ext:(none)", () => ShellIcon("file", useAttributes: true, directory: false));
        return Cached("ext:" + ext, () => ShellIcon("file" + ext, useAttributes: true, directory: false));
    }

    public static Bitmap? FolderIcon() =>
        Cached("folder", () => ShellIcon("folder", useAttributes: true, directory: true));

    /// <summary>既定ブラウザのアイコン</summary>
    public static Bitmap? BrowserIcon() => Cached("browser", () =>
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string? progId = uc?.GetValue("ProgId") as string;
            if (!string.IsNullOrEmpty(progId))
            {
                using var di = Registry.ClassesRoot.OpenSubKey(progId + @"\DefaultIcon");
                string? spec = di?.GetValue(null) as string;
                var bmp = FromIconSpec(spec);
                if (bmp is not null) return bmp;
            }
        }
        catch { }
        return ShellIcon("file.html", useAttributes: true, directory: false);
    });

    /// <summary>キャッシュ済みのファビコンを返す。無ければバックグラウンドで取得を始めて null</summary>
    public static Bitmap? Favicon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "http" && uri.Scheme != "https") return null;
        string host = uri.Host.ToLowerInvariant();
        string key = "fav:" + host;

        lock (Gate)
        {
            if (Mem.TryGetValue(key, out var bmp)) return bmp;
        }

        string cacheFile = FaviconCachePath(host, ".png");
        string noneFile = FaviconCachePath(host, ".none");
        try
        {
            if (File.Exists(cacheFile))
            {
                using var img = Image.FromFile(cacheFile);
                var bmp = Resize(img, Size);
                lock (Gate) Mem[key] = bmp;
                return bmp;
            }
            if (File.Exists(noneFile) && (DateTime.Now - File.GetLastWriteTime(noneFile)).TotalDays < 7)
            {
                lock (Gate) Mem[key] = null;
                return null;
            }
        }
        catch { }

        lock (Gate)
        {
            if (!PendingFavicons.Add(host)) return null;
        }
        _ = Task.Run(() => DownloadFaviconAsync(uri));
        return null;
    }

    /// <summary>ファビコンのキャッシュを捨てて取り直す</summary>
    public static void RefreshFavicon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        string host = uri.Host.ToLowerInvariant();
        lock (Gate) Mem.Remove("fav:" + host);
        try
        {
            File.Delete(FaviconCachePath(host, ".png"));
            File.Delete(FaviconCachePath(host, ".none"));
        }
        catch { }
        Favicon(url);
    }

    /// <summary>手動指定アイコンの変更などで、メモリキャッシュを捨てる</summary>
    public static void Invalidate(string path)
    {
        lock (Gate)
        {
            Mem.Remove("file:" + path);
            Mem.Remove("exe:" + path);
        }
    }

    // ---------- 内部 ----------

    private static Bitmap? Cached(string key, Func<Bitmap?> factory)
    {
        lock (Gate)
        {
            if (Mem.TryGetValue(key, out var hit)) return hit;
        }
        Bitmap? bmp = null;
        try { bmp = factory(); } catch (Exception ex) { Logger.Log($"アイコン取得失敗 {key}: {ex.Message}"); }
        lock (Gate) Mem[key] = bmp;
        return bmp;
    }

    private static bool IsLocal(string path)
    {
        if (path.StartsWith(@"\\")) return false;
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch { return false; }
    }

    private static Bitmap? ShellIcon(string path, bool useAttributes, bool directory)
    {
        var info = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
        uint attrs = 0;
        if (useAttributes)
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            attrs = directory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        }
        IntPtr r = NativeMethods.SHGetFileInfo(path, attrs, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), flags);
        if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            using var ico = Icon.FromHandle(info.hIcon);
            return Resize(ico.ToBitmap(), Size, disposeSource: true);
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>"C:\path\app.exe,3" 形式の DefaultIcon 値から取得</summary>
    private static Bitmap? FromIconSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        string s = spec.Trim().Trim('"');
        int index = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s.Substring(comma + 1).Trim(), out int idx))
        {
            index = idx;
            s = s.Substring(0, comma).Trim().Trim('"');
        }
        s = Environment.ExpandEnvironmentVariables(s);
        if (!File.Exists(s)) return null;
        if (s.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var ico = new Icon(s, Size, Size);
            return ico.ToBitmap();
        }
        NativeMethods.ExtractIconEx(s, index, out IntPtr large, out IntPtr small, 1);
        IntPtr h = large != IntPtr.Zero ? large : small;
        if (h == IntPtr.Zero) return null;
        try
        {
            using var ico = Icon.FromHandle(h);
            return Resize(ico.ToBitmap(), Size, disposeSource: true);
        }
        finally
        {
            if (large != IntPtr.Zero) NativeMethods.DestroyIcon(large);
            if (small != IntPtr.Zero) NativeMethods.DestroyIcon(small);
        }
    }

    private static Bitmap Resize(Image src, int size, bool disposeSource = false)
    {
        try
        {
            if (src.Width == size && src.Height == size && src is Bitmap b && !disposeSource)
                return (Bitmap)b.Clone();
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            // 縦横比を保って中央に
            float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            g.DrawImage(src, (size - w) / 2, (size - h) / 2, w, h);
            return bmp;
        }
        finally
        {
            if (disposeSource) src.Dispose();
        }
    }

    private static string FaviconCachePath(string host, string ext)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in host)
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
        return Path.Combine(AppSettings.IconCacheDirectory, sb + ext);
    }

    private static async Task DownloadFaviconAsync(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        Bitmap? result = null;
        try
        {
            Directory.CreateDirectory(AppSettings.IconCacheDirectory);

            // 1) Chrome のファビコン DB（社内サイトも拾える・通信不要）
            try
            {
                byte[]? png = ChromeData.ReadFavicon(ChromeProfile, host);
                if (png is not null) result = DecodeImage(png);
                if (result is not null) Logger.Log($"favicon Chrome DB から取得 {host}");
            }
            catch (Exception ex)
            {
                Logger.Log($"favicon Chrome DB 読み取り失敗 {host}: {ex.Message}");
            }

            // 2) サイトから取得
            var candidates = new List<string>
            {
                $"{uri.Scheme}://{uri.Authority}/favicon.ico",
            };
            if (uri.Scheme == "http") candidates.Add($"https://{uri.Authority}/favicon.ico");
            else candidates.Add($"http://{uri.Authority}/favicon.ico");

            // ページの <link rel="icon"> も候補にする
            if (result is null)
            {
                string? linked = await FindLinkedIconAsync(uri).ConfigureAwait(false);
                if (linked is not null) candidates.Insert(0, linked);
            }

            foreach (string c in candidates)
            {
                if (result is not null) break;
                try
                {
                    byte[] data = await Http.GetByteArrayAsync(c).ConfigureAwait(false);
                    if (data.Length < 16) continue;
                    result = DecodeImage(data);
                    if (result is not null) break;
                }
                catch { }
            }

            if (result is not null)
            {
                result.Save(FaviconCachePath(host, ".png"), ImageFormat.Png);
                Logger.Log($"favicon 取得 {host}");
            }
            else
            {
                File.WriteAllText(FaviconCachePath(host, ".none"), DateTime.Now.ToString("s"));
                Logger.Log($"favicon なし {host}");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"favicon 取得失敗 {host}: {ex.Message}");
        }
        finally
        {
            lock (Gate)
            {
                Mem["fav:" + host] = result;
                PendingFavicons.Remove(host);
            }
            if (result is not null)
                FaviconArrived?.Invoke();
        }
    }

    private static async Task<string?> FindLinkedIconAsync(Uri uri)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var ct = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!ct.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            byte[] buf = new byte[64 * 1024];
            int total = 0, n;
            while (total < buf.Length && (n = await stream.ReadAsync(buf, total, buf.Length - total).ConfigureAwait(false)) > 0)
                total += n;
            string html = System.Text.Encoding.UTF8.GetString(buf, 0, total);
            var m = System.Text.RegularExpressions.Regex.Match(html,
                @"<link[^>]+rel\s*=\s*[""'](?:shortcut icon|icon|apple-touch-icon)[""'][^>]*>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
                m = System.Text.RegularExpressions.Regex.Match(html,
                    @"<link[^>]+href\s*=\s*[""'][^""']*favicon[^""']*[""'][^>]*>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var href = System.Text.RegularExpressions.Regex.Match(m.Value, @"href\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!href.Success) return null;
            if (Uri.TryCreate(res.RequestMessage?.RequestUri ?? uri, href.Groups[1].Value, out var abs))
                return abs.ToString();
        }
        catch { }
        return null;
    }

    private static Bitmap? DecodeImage(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            // ICO
            if (data.Length > 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
            {
                try
                {
                    using var ico = new Icon(ms, Size, Size);
                    return ico.ToBitmap();
                }
                catch
                {
                    ms.Position = 0;
                }
            }
            using var img = Image.FromStream(ms);
            return Resize(img, Size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>リンク切れ表示用に、右下へ警告マークを重ねる</summary>
    public static Bitmap WithWarningBadge(Bitmap? baseImage)
    {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (baseImage is not null)
        {
            using var ia = new ImageAttributes();
            var cm = new ColorMatrix { Matrix33 = 0.45f };
            ia.SetColorMatrix(cm);
            g.DrawImage(baseImage, new Rectangle(0, 0, Size, Size), 0, 0, baseImage.Width, baseImage.Height, GraphicsUnit.Pixel, ia);
        }
        int d = Size / 2;
        var rect = new Rectangle(Size - d, Size - d, d - 1, d - 1);
        using var fill = new SolidBrush(Color.FromArgb(255, 230, 160, 20));
        using var pen = new Pen(Color.White, 1.5f);
        g.FillEllipse(fill, rect);
        g.DrawEllipse(pen, rect);
        using var f = new Font("Segoe UI", d * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("!", f, Brushes.White, new RectangleF(rect.X, rect.Y - 0.5f, rect.Width, rect.Height), sf);
        return bmp;
    }

    /// <summary>WPF 用に BitmapSource へ変換</summary>
    public static System.Windows.Media.Imaging.BitmapSource? ToBitmapSource(Bitmap? bmp)
    {
        if (bmp is null) return null;
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var bi = new System.Windows.Media.Imaging.BitmapImage();
        bi.BeginInit();
        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
