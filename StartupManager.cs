using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace LunchBox;

/// <summary>いま管理者権限（昇格）で動いているか</summary>
internal static class Elevation
{
    private static bool? _cached;
    public static bool IsElevated
    {
        get
        {
            if (_cached is null)
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    _cached = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { _cached = false; }
            }
            return _cached.Value;
        }
    }
}

/// <summary>
/// 自動起動と、管理者権限での常駐を管理する。
/// ・一般権限で常駐：HKCU の Run キー
/// ・管理者権限で常駐：タスク スケジューラ（最上位の特権）。UAC なしで昇格起動できる。
///   タスクは「管理者権限で常駐」が ON の間ずっと置いておき、サインイン時の自動起動はトリガーの有効／無効で切り替える。
/// </summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "LunchBox";

    public static void Apply(bool startWithWindows, bool runElevated)
    {
        if (runElevated)
        {
            SetRunKey(false);
            if (Elevation.IsElevated) CreateTask(startWithWindows);
            // 昇格していないとタスクは作れない（昇格して起動し直したときに作る）
        }
        else
        {
            SetRunKey(startWithWindows);
            if (TaskExists())
            {
                int code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                if (code != 0) Logger.Log($"昇格用タスクを削除できませんでした (exit={code})。管理者で実行中に設定を保存すると削除されます。");
            }
        }
    }

    public static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>旧 FrontSwitcher の自動起動タスクを削除する（管理者で動いているときだけ成功する）。削除できた／元から無ければ true</summary>
    public static bool RemoveFrontSwitcherTask()
    {
        if (RunSchtasks("/Query /TN \"FrontSwitcher\"") != 0) return true;
        RunSchtasks("/End /TN \"FrontSwitcher\"");
        int code = RunSchtasks("/Delete /TN \"FrontSwitcher\" /F");
        Logger.Log(code == 0 ? "FrontSwitcher の自動起動タスクを削除しました" : $"FrontSwitcher のタスクを削除できませんでした (exit={code})");
        return code == 0;
    }

    /// <summary>昇格用タスクを実行する（UAC なしで管理者として起動）</summary>
    public static bool RunTask() => RunSchtasks($"/Run /TN \"{TaskName}\"") == 0;

    private static void SetRunKey(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) key.SetValue(AppSettings.AppName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(AppSettings.AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("スタートアップ登録の変更に失敗: " + ex.Message);
        }
    }

    private static void CreateTask(bool logonTriggerEnabled)
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return;

        // schtasks のコマンドラインでは電源条件を指定できず、既定で「バッテリー駆動で停止」「72時間で強制終了」が付く。
        // 常駐が突然消える原因になるので、XML 定義で明示的に無効化して登録する（FrontSwitcher と同じ）。
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>{(logonTriggerEnabled ? "true" : "false")}</Enabled>
      <Delay>PT15S</Delay>
      <UserId>{SecurityElement.Escape(user)}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <StartWhenAvailable>false</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{SecurityElement.Escape("\"" + exe + "\"")}</Command>
    </Exec>
  </Actions>
</Task>
""";
        string tmp = Path.Combine(Path.GetTempPath(), "LunchBoxTask.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            Logger.Log(code == 0 ? $"昇格用タスクを登録/更新（サインイン時の自動起動={logonTriggerEnabled}）" : $"昇格用タスクの作成に失敗 (exit={code})");
        }
        catch (Exception ex)
        {
            Logger.Log("昇格用タスク作成エラー: " + ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Logger.Log("schtasks 実行エラー: " + ex.Message);
            return -1;
        }
    }
}
