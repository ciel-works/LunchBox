using System.IO;

namespace LunchBox;

/// <summary>診断用の簡易ログ。%AppData%\LunchBox\log.txt に追記する。</summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(AppSettings.SettingsDirectory, "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.SettingsDirectory);
                // 1MB を超えたら古い分を捨てる
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(LogPath, Path.Combine(AppSettings.SettingsDirectory, "log.old.txt"), overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ失敗は無視
        }
    }
}
