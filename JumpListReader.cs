using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace LunchBox;

/// <summary>
/// Windows のジャンプリスト実体（%AppData%\Microsoft\Windows\Recent\AutomaticDestinations\&lt;AppID&gt;.automaticDestinations-ms）
/// を読み、アプリごとの「最近開いた項目」を取り出す。
/// ・ファイルは OLE 複合ファイル。"DestList" ストリームに「パス＋最終アクセス日時＋ピン状態」が並ぶので、LNK は解析しない。
/// ・AppID は AppUserModelID（無ければ exe のフルパス大文字）の UTF-16LE を CRC64 したもの。
/// </summary>
internal static class JumpListReader
{
    public static string AutomaticDestinationsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Recent\AutomaticDestinations");

    // ---------- AppID ----------

    private static readonly ulong[] Crc64Table = BuildTable();

    private static ulong[] BuildTable()
    {
        const ulong poly = 0x92C64265D32139A4UL;
        var t = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong c = (ulong)i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>AppUserModelID または exe パスから AppID（16桁の16進）を求める</summary>
    public static string ComputeAppId(string appUserModelIdOrPath)
    {
        ulong crc = 0xFFFFFFFFFFFFFFFFUL;
        foreach (byte b in Encoding.Unicode.GetBytes(appUserModelIdOrPath.ToUpperInvariant()))
            crc = Crc64Table[(int)((crc ^ b) & 0xFF)] ^ (crc >> 8);
        return crc.ToString("x16");
    }

    /// <summary>よく使うアプリの既知の AppUserModelID（exe 名で引く）</summary>
    private static readonly Dictionary<string, string[]> KnownAumids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["excel.exe"] = new[] { "Microsoft.Office.EXCEL.EXE.15" },
        ["winword.exe"] = new[] { "Microsoft.Office.WINWORD.EXE.15" },
        ["powerpnt.exe"] = new[] { "Microsoft.Office.POWERPNT.EXE.15" },
        ["outlook.exe"] = new[] { "Microsoft.Office.OUTLOOK.EXE.15" },
        ["onenote.exe"] = new[] { "Microsoft.Office.ONENOTE.EXE.15" },
        ["msaccess.exe"] = new[] { "Microsoft.Office.MSACCESS.EXE.15" },
        ["explorer.exe"] = new[] { "Microsoft.Windows.Explorer" },
        ["chrome.exe"] = new[] { "Chrome" },
        ["msedge.exe"] = new[] { "MSEdge" },
        ["acrobat.exe"] = new[] { "Acrobat.exe" },
        ["acrord32.exe"] = new[] { "AcroRd32.exe" },
        ["notepad.exe"] = new[] { "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App" },
        ["code.exe"] = new[] { "Microsoft.VisualStudioCode" },
    };

    /// <summary>このアプリのジャンプリストとして候補になる AppID（存在するものだけ）</summary>
    public static List<string> CandidateAppIds(AppEntry app)
    {
        var ids = new List<string>();
        void Add(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return;
            string id = ComputeAppId(src);
            if (!ids.Contains(id)) ids.Add(id);
        }
        Add(app.AppUserModelId);
        string exeName = "";
        try { exeName = Path.GetFileName(app.ExePath); } catch { }
        if (KnownAumids.TryGetValue(exeName, out var aumids))
            foreach (var a in aumids) Add(a);
        Add(app.ExePath);
        return ids.Where(id => File.Exists(JumpListPath(id))).ToList();
    }

    public static string JumpListPath(string appId) =>
        Path.Combine(AutomaticDestinationsDirectory, appId + ".automaticDestinations-ms");

    /// <summary>このアプリのジャンプリストが変わったかの目印（ファイルの更新日時・大きさ）。読み直すかどうかの判定用</summary>
    public static long Stamp(AppEntry app)
    {
        long s = 17;
        foreach (string id in CandidateAppIds(app))
            s = Stamps.Mix(s, Stamps.OfFile(JumpListPath(id)));
        return s;
    }

    // ---------- DestList ----------

    public sealed record Entry(string Path, DateTime LastAccess, bool Pinned);

    /// <summary>アプリの候補 AppID すべてのジャンプリストから項目を集める。forceKind を渡すと種類を固定（エクスプローラー＝フォルダ）</summary>
    public static List<RecentItem> Read(AppEntry app, ItemKind? forceKind = null)
    {
        var result = new List<RecentItem>();
        foreach (string id in CandidateAppIds(app))
        {
            try
            {
                foreach (var e in ReadDestList(JumpListPath(id)))
                {
                    string p = e.Path;
                    if (string.IsNullOrWhiteSpace(p) || p.StartsWith("::")) continue;
                    ItemKind kind;
                    if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        kind = ItemKind.Url;
                    else if (p.StartsWith("knownfolder:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    else if (p.Length < 2 || (p[1] != ':' && !p.StartsWith(@"\\")))
                        continue; // シェル項目などパスでないもの
                    else
                        kind = forceKind ?? GuessKind(p);
                    result.Add(new RecentItem { Target = p, Kind = kind, When = e.LastAccess });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ジャンプリスト読み取り失敗 {id}: {ex.Message}");
            }
        }
        return result
            .GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.When).First())
            .OrderByDescending(r => r.When)
            .ToList();
    }

    /// <summary>
    /// ファイルかフォルダかを推定する。ローカルドライブなら実体を見る（「R9新年会_2027.01.15」のような
    /// ピリオド入りフォルダ名を拡張子と誤認しないため）。ネットワーク上は触らず拡張子で判断。
    /// </summary>
    private static ItemKind GuessKind(string path)
    {
        try
        {
            bool local = path.Length >= 2 && path[1] == ':' && !path.StartsWith(@"\\");
            if (local)
            {
                string? root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType != DriveType.Network)
                {
                    if (Directory.Exists(path)) return ItemKind.Folder;
                    if (File.Exists(path)) return ItemKind.File;
                }
            }
        }
        catch { }
        return string.IsNullOrEmpty(Path.GetExtension(path)) ? ItemKind.Folder : ItemKind.File;
    }

    /// <summary>DestList ストリームを解析する（Windows 10/11 の v3/v4 と Windows 7 の v1 に対応）</summary>
    public static List<Entry> ReadDestList(string file)
    {
        var entries = new List<Entry>();
        byte[]? data = ReadStream(file, "DestList");
        if (data is null || data.Length < 32) return entries;

        int version = BitConverter.ToInt32(data, 0);
        int pos = 32;
        while (pos + 114 <= data.Length)
        {
            try
            {
                int entryStart = pos;
                pos += 8;            // checksum
                pos += 16 * 4;       // droid / birth droid
                pos += 16;           // NetBIOS name
                pos += 4;            // entry number
                pos += 4;            // unknown
                pos += 4;            // access count (float)
                long ft = BitConverter.ToInt64(data, pos); pos += 8;
                int pin = BitConverter.ToInt32(data, pos); pos += 4;
                if (version >= 3)
                {
                    pos += 4;        // unknown
                    pos += 4;        // access count
                    pos += 8;        // unknown
                }
                if (pos + 2 > data.Length) break;
                int len = BitConverter.ToUInt16(data, pos); pos += 2;
                if (len < 0 || pos + len * 2 > data.Length) break;
                string path = Encoding.Unicode.GetString(data, pos, len * 2);
                pos += len * 2;
                if (version >= 3) pos += 4; // padding

                DateTime when;
                try { when = ft > 0 ? DateTime.FromFileTime(ft) : DateTime.MinValue; } catch { when = DateTime.MinValue; }
                entries.Add(new Entry(path.TrimEnd('\0'), when, pin >= 0));
                if (pos <= entryStart) break;
            }
            catch
            {
                break;
            }
        }
        return entries;
    }

    // ---------- OLE 複合ファイルのストリーム読み取り（ole32 の構造化ストレージを使う） ----------

    private static byte[]? ReadStream(string file, string streamName)
    {
        // Explorer が開いていることがあるので、コピーしてから読む
        string tmp = Path.Combine(Path.GetTempPath(), "lunchbox_jl_" + Path.GetFileName(file));
        try
        {
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var d = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                s.CopyTo(d);
        }
        catch (Exception ex)
        {
            Logger.Log($"ジャンプリストのコピー失敗 {file}: {ex.Message}");
            return null;
        }

        try
        {
            int hr = StgOpenStorageEx(tmp, STGM_READ | STGM_SHARE_DENY_WRITE, STGFMT_STORAGE, 0, IntPtr.Zero, IntPtr.Zero,
                ref IID_IStorage, out object storageObj);
            if (hr != 0 || storageObj is not IStorage storage) return null;
            try
            {
                hr = storage.OpenStream(streamName, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out IStream stream);
                if (hr != 0 || stream is null) return null;
                try
                {
                    stream.Stat(out STATSTG stat, 1);
                    long size = stat.cbSize;
                    if (size <= 0 || size > 64 * 1024 * 1024) return null;
                    var buf = new byte[size];
                    IntPtr pRead = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        int total = 0;
                        // 読み取り用の入れ物は 1 つを使い回す（回ごとに作らない）
                        var chunk = new byte[Math.Min(65536, size)];
                        while (total < size)
                        {
                            stream.Read(chunk, (int)Math.Min(chunk.Length, size - total), pRead);
                            int read = Marshal.ReadInt32(pRead);
                            if (read <= 0) break;
                            Array.Copy(chunk, 0, buf, total, read);
                            total += read;
                        }
                        if (total < size) Array.Resize(ref buf, total);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pRead);
                    }
                    return buf;
                }
                finally
                {
                    Marshal.ReleaseComObject(stream);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(storage);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private const uint STGM_READ = 0x00000000;
    private const uint STGM_SHARE_DENY_WRITE = 0x00000020;
    private const uint STGM_SHARE_EXCLUSIVE = 0x00000010;
    private const uint STGFMT_STORAGE = 0;
    private static Guid IID_IStorage = new("0000000B-0000-0000-C000-000000000046");

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgOpenStorageEx(string pwcsName, uint grfMode, uint stgfmt, uint grfAttrs,
        IntPtr pStgOptions, IntPtr reserved2, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppObjectOpen);

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        [PreserveSig] int CreateStream(string pwcsName, uint grfMode, uint reserved1, uint reserved2, out IStream ppstm);
        [PreserveSig] int OpenStream(string pwcsName, IntPtr reserved1, uint grfMode, uint reserved2, out IStream ppstm);
        [PreserveSig] int CreateStorage(string pwcsName, uint grfMode, uint reserved1, uint reserved2, out IStorage ppstg);
        [PreserveSig] int OpenStorage(string pwcsName, IStorage? pstgPriority, uint grfMode, IntPtr snbExclude, uint reserved, out IStorage ppstg);
        [PreserveSig] int CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr snbExclude, IStorage pstgDest);
        [PreserveSig] int MoveElementTo(string pwcsName, IStorage pstgDest, string pwcsNewName, uint grfFlags);
        [PreserveSig] int Commit(uint grfCommitFlags);
        [PreserveSig] int Revert();
        [PreserveSig] int EnumElements(uint reserved1, IntPtr reserved2, uint reserved3, out IntPtr ppenum);
        [PreserveSig] int DestroyElement(string pwcsName);
        [PreserveSig] int RenameElement(string pwcsOldName, string pwcsNewName);
        [PreserveSig] int SetElementTimes(string pwcsName, IntPtr pctime, IntPtr patime, IntPtr pmtime);
        [PreserveSig] int SetClass(ref Guid clsid);
        [PreserveSig] int SetStateBits(uint grfStateBits, uint grfMask);
        [PreserveSig] int Stat(out STATSTG pstatstg, uint grfStatFlag);
    }
}
