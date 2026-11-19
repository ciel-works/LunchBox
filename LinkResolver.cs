using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LunchBox;

/// <summary>
/// リンク切れ対策。
/// ・保存時：ドライブ文字 → UNC に正規化し、NTFS のファイル ID を記録
/// ・確認時：パスが無ければ ファイル ID → 同名探索 の順に探し、見つかれば Target を更新
/// </summary>
internal static class LinkResolver
{
    /// <summary>Z:\a\b → \\server\share\a\b。すでに UNC ならそのまま。ローカルは null</summary>
    public static string? ToUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\\")) return path;
        if (path.Length < 2 || path[1] != ':') return null;
        string drive = path.Substring(0, 2);
        string rest = path.Substring(2).TrimStart('\\');
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            int rc = NativeMethods.WNetGetConnection(drive, sb, ref len);
            if (rc == 0)
            {
                string remote = sb.ToString().TrimEnd('\\');
                return rest.Length == 0 ? remote : remote + @"\" + rest;
            }
        }
        catch { }

        // 予備：デバイス名から UNC を取り出す（\Device\LanmanRedirector\;Z:000...\server\share 形式）
        try
        {
            var sb = new StringBuilder(2048);
            uint n = NativeMethods.QueryDosDevice(drive, sb, (uint)sb.Capacity);
            if (n > 0)
            {
                string dev = sb.ToString();
                int marker = dev.IndexOf(@"\;" + drive, StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    int slash = dev.IndexOf('\\', marker + 2);
                    if (slash > 0)
                    {
                        string remote = @"\" + dev.Substring(slash).TrimEnd('\\'); // "\server\share" → "\\server\share"
                        if (remote.StartsWith(@"\\") && remote.Length > 3)
                            return rest.Length == 0 ? remote : remote + @"\" + rest;
                    }
                }
            }
        }
        catch { }
        return null; // ネットワークドライブではない（仮想ドライブ等は変換不可）
    }

    /// <summary>登録・編集時に呼ぶ。UNC とファイル ID を記録する</summary>
    public static void CaptureIdentity(PinItem pin)
    {
        if (pin.Kind == ItemKind.Url)
        {
            pin.TargetUnc = null;
            pin.VolumeId = pin.FileId = pin.VolumeRoot = null;
            pin.Status = LinkStatus.Ok;
            pin.LastChecked = DateTime.Now;
            return;
        }

        pin.TargetUnc = ToUnc(pin.Target);
        string path = pin.EffectiveTarget;
        if (!PinItem.PathExists(path))
        {
            pin.Status = LinkStatus.NotFound;
            pin.LastChecked = DateTime.Now;
            return;
        }

        var id = ReadFileId(path);
        if (id is not null)
        {
            pin.VolumeId = id.Value.VolumeSerialNumber.ToString("X16");
            pin.FileId = id.Value.FileIdHigh.ToString("X16") + id.Value.FileIdLow.ToString("X16");
            pin.VolumeRoot = VolumeRootOf(pin.TargetUnc ?? path);
        }
        pin.Status = LinkStatus.Ok;
        pin.LastChecked = DateTime.Now;
    }

    /// <summary>リンクを確認し、必要なら追跡して Target を更新する。戻り値は新しい状態</summary>
    public static LinkStatus Check(PinItem pin)
    {
        if (pin.Kind == ItemKind.Url)
        {
            pin.Status = LinkStatus.Ok;
            pin.LastChecked = DateTime.Now;
            return pin.Status;
        }

        try
        {
            if (PinItem.PathExists(pin.Target) || PinItem.PathExists(pin.TargetUnc))
            {
                // 見つかった。ID 未記録なら今のうちに記録
                if (string.IsNullOrEmpty(pin.FileId))
                    CaptureIdentity(pin);
                pin.Status = pin.Status == LinkStatus.Relinked ? LinkStatus.Relinked : LinkStatus.Ok;
                pin.LastChecked = DateTime.Now;
                return pin.Status;
            }

            string? found = FindByFileId(pin) ?? FindByName(pin);
            if (found is not null)
            {
                Logger.Log($"再リンク: {pin.Target} -> {found}");
                ApplyNewPath(pin, found);
                pin.Status = LinkStatus.Relinked;
                pin.LastChecked = DateTime.Now;
                return pin.Status;
            }

            pin.Status = LinkStatus.NotFound;
            pin.LastChecked = DateTime.Now;
            return pin.Status;
        }
        catch (Exception ex)
        {
            Logger.Log($"リンク確認エラー {pin.Target}: {ex.Message}");
            pin.Status = LinkStatus.NotFound;
            pin.LastChecked = DateTime.Now;
            return pin.Status;
        }
    }

    /// <summary>新しいパスを、元の表記（ドライブ文字）に合わせて Target に反映</summary>
    private static void ApplyNewPath(PinItem pin, string newPath)
    {
        string display = newPath;
        // 元が Z:\... で、新パスがその UNC 配下なら Z:\ 表記に戻す
        if (pin.Target.Length >= 2 && pin.Target[1] == ':' && !string.IsNullOrEmpty(pin.TargetUnc))
        {
            string? driveUnc = ToUnc(pin.Target.Substring(0, 3)); // "Z:\" → "\\srv\share"
            if (!string.IsNullOrEmpty(driveUnc) &&
                newPath.StartsWith(driveUnc, StringComparison.OrdinalIgnoreCase))
            {
                display = pin.Target.Substring(0, 2) + newPath.Substring(driveUnc.Length);
            }
        }
        pin.Target = display;
        pin.TargetUnc = ToUnc(display) ?? (newPath.StartsWith(@"\\") ? newPath : null);
        var id = ReadFileId(newPath);
        if (id is not null)
        {
            pin.VolumeId = id.Value.VolumeSerialNumber.ToString("X16");
            pin.FileId = id.Value.FileIdHigh.ToString("X16") + id.Value.FileIdLow.ToString("X16");
            pin.VolumeRoot = VolumeRootOf(pin.TargetUnc ?? newPath);
        }
    }

    // ---------- ファイル ID ----------

    private static NativeMethods.FILE_ID_INFO? ReadFileId(string path)
    {
        try
        {
            using var h = NativeMethods.CreateFile(path, NativeMethods.FILE_READ_ATTRIBUTES,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (NativeMethods.GetFileInformationByHandleEx(h, NativeMethods.FileIdInfo, out var info,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FILE_ID_INFO>()))
                return info;

            // 予備：128bit ID 非対応のドライブ（Box Drive 等）は従来の 64bit ID を使う
            if (NativeMethods.GetFileInformationByHandle(h, out var legacy) &&
                (legacy.nFileIndexHigh != 0 || legacy.nFileIndexLow != 0))
            {
                return new NativeMethods.FILE_ID_INFO
                {
                    VolumeSerialNumber = legacy.dwVolumeSerialNumber,
                    FileIdLow = ((ulong)legacy.nFileIndexHigh << 32) | legacy.nFileIndexLow,
                    FileIdHigh = 0,
                };
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindByFileId(PinItem pin)
    {
        if (string.IsNullOrEmpty(pin.FileId)) return null;
        if (pin.FileId.Length != 32) return null;
        try
        {
            // 同じボリューム上の「何か」のハンドルがあれば ID で開ける。
            // Box Drive のように C:\ 配下にマウントされた別ボリュームもあるので、
            // 元ファイルの親フォルダ → その上 → … → ボリュームルート の順に試す。
            var hints = new List<string>();
            string basePath = pin.TargetUnc ?? pin.Target;
            try
            {
                string? d = Path.GetDirectoryName(basePath.TrimEnd('\\'));
                while (!string.IsNullOrEmpty(d))
                {
                    hints.Add(d);
                    d = Path.GetDirectoryName(d);
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(pin.VolumeRoot)) hints.Add(pin.VolumeRoot);

            SafeFileHandle? h = null;
            foreach (string hint in hints.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(hint)) continue;
                using var vol = NativeMethods.CreateFile(hint, NativeMethods.FILE_READ_ATTRIBUTES,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (vol.IsInvalid) continue;

                var desc = new NativeMethods.FILE_ID_DESCRIPTOR
                {
                    dwSize = 24,
                    Type = 2, // ExtendedFileIdType
                    IdHigh = Convert.ToUInt64(pin.FileId.Substring(0, 16), 16),
                    IdLow = Convert.ToUInt64(pin.FileId.Substring(16, 16), 16),
                };
                var opened = NativeMethods.OpenFileById(vol, ref desc, NativeMethods.FILE_READ_ATTRIBUTES,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                    IntPtr.Zero, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS);
                if (!opened.IsInvalid)
                {
                    h = opened;
                    break;
                }
                Logger.Log($"OpenFileById 失敗 hint={hint} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                opened.Dispose();
            }
            if (h is null) return null;
            using var _ = h;

            var sb = new StringBuilder(2048);
            uint n = NativeMethods.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n > sb.Capacity) return null;
            string p = sb.ToString();
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\")) p = p.Substring(4);

            // 同じボリュームの別ファイルに ID が再利用されていないか、種類で軽く確認
            bool isDir = Directory.Exists(p);
            if (pin.Kind == ItemKind.Folder && !isDir) return null;
            if (pin.Kind == ItemKind.File && isDir) return null;
            return p;
        }
        catch (Exception ex)
        {
            Logger.Log($"FileId 追跡失敗 {pin.Target}: {ex.Message}");
            return null;
        }
    }

    // ---------- 同名探索 ----------

    /// <summary>元のフォルダとその配下（2階層）から同名のものを探す</summary>
    private static string? FindByName(PinItem pin)
    {
        string path = pin.TargetUnc ?? pin.Target;
        string name;
        string? parent;
        try
        {
            string trimmed = path.TrimEnd('\\');
            name = Path.GetFileName(trimmed);
            parent = Path.GetDirectoryName(trimmed);
        }
        catch { return null; }
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent)) return null;

        // 親が無ければ、さらに上の階層から探す（1段だけ）
        if (!Directory.Exists(parent))
        {
            string? grand = null;
            try { grand = Path.GetDirectoryName(parent); } catch { }
            if (string.IsNullOrEmpty(grand) || !Directory.Exists(grand)) return null;
            parent = grand;
        }

        int budget = 3000;
        return Search(parent, name, pin.Kind == ItemKind.Folder, depth: 2, ref budget);
    }

    private static string? Search(string dir, string name, bool wantDir, int depth, ref int budget)
    {
        if (budget <= 0 || depth < 0) return null;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return null; }

        var subdirs = new List<string>();
        foreach (string e in entries)
        {
            if (--budget <= 0) return null;
            bool isDir;
            try { isDir = Directory.Exists(e); } catch { continue; }
            if (isDir == wantDir && string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase))
                return e;
            if (isDir) subdirs.Add(e);
        }
        if (depth == 0) return null;
        foreach (string sd in subdirs)
        {
            string? r = Search(sd, name, wantDir, depth - 1, ref budget);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>\\srv\share\a\b → \\srv\share\ 、C:\a → C:\</summary>
    public static string? VolumeRootOf(string path)
    {
        try
        {
            if (path.StartsWith(@"\\"))
            {
                var parts = path.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) return @"\\" + parts[0] + @"\" + parts[1] + @"\";
                return null;
            }
            return Path.GetPathRoot(path);
        }
        catch
        {
            return null;
        }
    }
}
