using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LunchBox;

/// <summary>
/// Windows の「すべてのアプリ」（shell:AppsFolder）の列挙と、そこからのアイコン取得。
/// ペイント・電卓などのストアアプリは exe の場所が無いので、AppUserModelID で扱う。
/// 起動するものには "shell:AppsFolder\&lt;AUMID&gt;" を入れておく。
/// </summary>
public static class ShellApps
{
    public const string Prefix = @"shell:AppsFolder\";

    public sealed record Entry(string Name, string Path)
    {
        /// <summary>LunchBox の「起動するもの」に入れる文字列</summary>
        public string LaunchTarget => Prefix + Path;
        /// <summary>ストアアプリ（パッケージ!App 形式）か</summary>
        public bool IsPackaged => Path.Contains('!');
    }

    public static bool IsShellApp(string? target) =>
        !string.IsNullOrEmpty(target) && target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    /// <summary>「すべてのアプリ」を列挙（名前順）</summary>
    public static List<Entry> Enumerate()
    {
        var list = new List<Entry>();
        Type? t = Type.GetTypeFromProgID("Shell.Application");
        if (t is null) return list;
        dynamic? shell = Activator.CreateInstance(t);
        if (shell is null) return list;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return list;
            dynamic items = folder.Items();
            int count = (int)items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? it = null;
                try
                {
                    it = items.Item(i);
                    if (it is null) continue;
                    string name = (string)(it.Name ?? "");
                    string path = (string)(it.Path ?? "");
                    if (name.Length == 0 || path.Length == 0) continue;
                    list.Add(new Entry(name, path));
                }
                catch { }
                finally
                {
                    if (it is not null) try { Marshal.ReleaseComObject(it); } catch { }
                }
            }
            try { Marshal.ReleaseComObject(items); } catch { }
            try { Marshal.ReleaseComObject(folder); } catch { }
        }
        catch (Exception ex)
        {
            Logger.Log("すべてのアプリの列挙失敗: " + ex.Message);
        }
        finally
        {
            try { Marshal.ReleaseComObject(shell); } catch { }
        }
        return list
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---------- アイコン ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int cb, out BITMAP bm);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_ICONONLY = 0x04;

    /// <summary>shell:AppsFolder\... や通常のパスからアイコンを取得（32px、アルファ付き）</summary>
    public static Bitmap? GetIcon(string parsingName, int size = 32)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            if (hr != 0 || factory is null) return null;
            try
            {
                hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_RESIZETOFIT, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                return FromHBitmapWithAlpha(hbm);
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"アプリのアイコン取得失敗 {parsingName}: {ex.Message}");
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }

    /// <summary>32bit DIB セクションのビット列から、アルファを保ったまま Bitmap を作る</summary>
    private static Bitmap? FromHBitmapWithAlpha(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero)
        {
            using var plain = Image.FromHbitmap(hbm);
            return new Bitmap(plain);
        }
        // 上下逆（ボトムアップ）の DIB を考慮して、負のストライドで読む
        var bmp = new Bitmap(bm.bmWidth, bm.bmHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bm.bmWidth, bm.bmHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bm.bmWidth * 4;
            var row = new byte[rowBytes];
            bool bottomUp = bm.bmHeight > 0;
            int h = Math.Abs(bm.bmHeight);
            for (int y = 0; y < h; y++)
            {
                int srcRow = bottomUp ? (h - 1 - y) : y;
                Marshal.Copy(bm.bmBits + srcRow * bm.bmWidthBytes, row, 0, rowBytes);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        // GetImage の結果はトップダウンのことが多い。上下が逆なら直す
        return bmp;
    }
}
