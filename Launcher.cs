using System.Diagnostics;
using System.IO;

namespace LunchBox;

/// <summary>項目・アプリを開く（Windows の関連付けに従う）</summary>
internal static class Launcher
{
    /// <summary>
    /// LunchBox が管理者権限で常駐しているとき、開くものまで管理者権限にならないよう
    /// エクスプローラー経由（一般権限）で開く。引数付きのときは使えないので false を返す。
    /// </summary>
    private static bool TryStartUnelevated(string target, string? arguments)
    {
        if (!Elevation.IsElevated) return false;
        if (!string.IsNullOrWhiteSpace(arguments)) return false;
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
        return true;
    }

    public static void OpenPin(PinItem pin)
    {
        string target = pin.EffectiveTarget;
        Logger.Log($"開く: [{pin.Kind}] {target} {pin.Arguments}");
        var psi = new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        };
        if (!string.IsNullOrWhiteSpace(pin.Arguments))
            psi.Arguments = pin.Arguments;
        if (pin.Kind == ItemKind.File)
        {
            try { psi.WorkingDirectory = Path.GetDirectoryName(target) ?? ""; } catch { }
        }
        if (TryStartUnelevated(target, pin.Arguments)) return;
        Process.Start(psi);
    }

    public static void OpenTarget(ItemKind kind, string target)
    {
        Logger.Log($"開く: [{kind}] {target}");
        if (TryStartUnelevated(target, null)) return;
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
    }

    public static void OpenApp(AppEntry app)
    {
        Logger.Log($"アプリ起動: {app.Name} ({app.ExePath}) admin={app.RunAsAdmin}");
        if (string.IsNullOrWhiteSpace(app.ExePath)) throw new InvalidOperationException("起動するものが未設定です。");

        // ストアアプリ・「すべてのアプリ」の項目：shell:AppsFolder\<AUMID> を ShellExecute で開く
        if (app.IsShellApp)
        {
            if (TryStartUnelevated(app.ExePath, null)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = app.ExePath, UseShellExecute = true });
            }
            catch
            {
                // 念のため explorer 経由でも試す
                Process.Start(new ProcessStartInfo("explorer.exe", app.ExePath) { UseShellExecute = true });
            }
            return;
        }

        var psi = new ProcessStartInfo { FileName = app.ExePath, UseShellExecute = true };
        try { psi.WorkingDirectory = Path.GetDirectoryName(app.ExePath) ?? ""; } catch { }
        if (!app.RunAsAdmin && TryStartUnelevated(app.ExePath, null)) return;
        if (app.RunAsAdmin) psi.Verb = "runas";
        try
        {
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC でキャンセルされた：エラーにしない
            Logger.Log("管理者実行がキャンセルされました");
        }
    }

    /// <summary>保存場所を開く（エクスプローラーで選択状態）。URL はサイトのトップ</summary>
    public static void OpenContaining(PinItem pin)
    {
        if (pin.Kind == ItemKind.Url)
        {
            if (Uri.TryCreate(pin.Target, UriKind.Absolute, out var uri))
                OpenTarget(ItemKind.Url, uri.GetLeftPart(UriPartial.Authority) + "/");
            return;
        }
        string target = pin.EffectiveTarget;
        if (PinItem.PathExists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            return;
        }
        // 実体が無ければ親フォルダ
        string? parent = null;
        try { parent = Path.GetDirectoryName(target.TrimEnd('\\')); } catch { }
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
        else
            throw new FileNotFoundException("保存場所が見つかりません。", target);
    }

    public static void CopyPath(PinItem pin)
    {
        try
        {
            System.Windows.Forms.Clipboard.SetText(pin.Target);
        }
        catch (Exception ex)
        {
            Logger.Log("クリップボードへのコピー失敗: " + ex.Message);
        }
    }
}
