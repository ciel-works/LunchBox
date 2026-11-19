using System.IO;
using Microsoft.Win32;

namespace LunchBox;

/// <summary>初回起動時に、よく使うアプリを自動検出して登録する</summary>
internal static class AppDetector
{
    private sealed record Known(string Name, string ExeName, HistorySource Source);

    private static readonly Known[] KnownApps =
    {
        new("Excel", "excel.exe", HistorySource.Excel),
        new("Word", "winword.exe", HistorySource.Word),
        new("PowerPoint", "powerpnt.exe", HistorySource.PowerPoint),
        new("エクスプローラー", "explorer.exe", HistorySource.Explorer),
        new("Chrome", "chrome.exe", HistorySource.Chrome),
    };

    public static List<AppEntry> Detect()
    {
        var list = new List<AppEntry>();
        foreach (var k in KnownApps)
        {
            string? exe = k.ExeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                : FindExe(k.ExeName);
            if (exe is null || !File.Exists(exe)) continue;
            list.Add(new AppEntry { Name = k.Name, ExePath = exe, Source = k.Source });
        }
        return list;
    }

    /// <summary>App Paths レジストリから exe の場所を探す</summary>
    public static string? FindExe(string exeName)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string sub in new[]
                     {
                         @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                         @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                     })
            {
                try
                {
                    using var key = root.OpenSubKey(sub);
                    if (key?.GetValue(null) is string p)
                    {
                        p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
        }
        return null;
    }
}
