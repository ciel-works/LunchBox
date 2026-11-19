using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
// WinForms/WPF 双方に同名型があるため WPF 側に固定する
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListBox = System.Windows.Controls.ListBox;

namespace LunchBox;

public partial class SwitcherSettingsWindow : Window
{
    private readonly SwitcherSettings _settings;
    private uint _pendingModifiers;
    private uint _pendingVk;

    /// <summary>保存時に確定した設定を通知する</summary>
    public event Action<SwitcherSettings>? SettingsSaved;

    public SwitcherSettingsWindow(SwitcherSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // 既存値を画面へ
        _pendingModifiers = settings.Modifiers;
        _pendingVk = settings.VirtualKey;
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
        ProcessNameBox.Text = settings.TargetProcessName;
        ExePathBox.Text = settings.TargetExePath;
        BringToFrontCheck.IsChecked = settings.BringTargetToFront;
        MinimizeCheck.IsChecked = settings.MinimizeCurrent;
        HideTaskbarCheck.IsChecked = settings.HideFromTaskbar;
        StartupCheck.IsChecked = settings.StartWithWindows;
        foreach (var n in settings.MinimizeWithProcesses)
            MinimizeListBox.Items.Add(n);
        foreach (var p in settings.CloseTabPatterns)
            CloseTabListBox.Items.Add(p);
        UpdateTargetPanelEnabled();
        UpdateMinimizeSubOptions();

        // マウスジェスチャ
        GestureEnableCheck.IsChecked = settings.MouseGestureEnabled;
        foreach (var g in settings.MouseGestures)
            GestureListBox.Items.Add(g.Clone());
        foreach (GestureAction a in Enum.GetValues<GestureAction>())
            GestureActionCombo.Items.Add(new ComboBoxItem { Content = GestureBinding.ActionToText(a), Tag = a });
        GestureActionCombo.SelectedIndex = 0;
        GestureStartBox.Text = settings.GestureStartDistance.ToString();
        GestureStrokeBox.Text = settings.GestureStrokeDistance.ToString();
        foreach (var n in settings.GestureExcludeProcesses)
            GestureExcludeListBox.Items.Add(n);
        UpdateGesturePatternText();
    }

    // --- マウスジェスチャ ---
    private string _pendingPattern = "";

    private void GestureDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string dir }) return;
        // 同じ向きが続くのは1ストロークなので追加しない
        if (_pendingPattern.Length > 0 && _pendingPattern[^1] == dir[0]) return;
        if (_pendingPattern.Length >= GestureBinding.MaxStrokes) return;
        _pendingPattern += dir;
        UpdateGesturePatternText();
    }

    private void GestureClearButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingPattern = "";
        UpdateGesturePatternText();
    }

    private void UpdateGesturePatternText()
    {
        bool empty = _pendingPattern.Length == 0;
        GesturePatternText.Text = empty ? "(未入力)" : GestureBinding.PatternToArrows(_pendingPattern);
        GesturePatternText.Foreground = empty ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.Black;
    }

    private void AddGestureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!GestureBinding.IsValidPattern(_pendingPattern))
        {
            Info("向きのボタン（↑↓←→）でジェスチャを入力してください。");
            return;
        }
        if (GestureActionCombo.SelectedItem is not ComboBoxItem { Tag: GestureAction action }) return;

        uint mods = 0;
        if (GestureCtrlCheck.IsChecked == true) mods |= HotKeyService.MOD_CONTROL;
        if (GestureShiftCheck.IsChecked == true) mods |= HotKeyService.MOD_SHIFT;
        if (GestureAltCheck.IsChecked == true) mods |= HotKeyService.MOD_ALT;
        if (GestureWinCheck.IsChecked == true) mods |= HotKeyService.MOD_WIN;

        var dup = GestureListBox.Items.Cast<GestureBinding>()
            .FirstOrDefault(g => g.Modifiers == mods && g.Pattern == _pendingPattern);
        if (dup is not null)
        {
            Info($"同じキーと向きのジェスチャが既にあります:\n{dup}");
            return;
        }
        if (GestureListBox.Items.Count >= SwitcherSettings.MaxGestures)
        {
            Info($"登録できるのは最大 {SwitcherSettings.MaxGestures} 件までです。");
            return;
        }

        GestureListBox.Items.Add(new GestureBinding(mods, _pendingPattern, action));
        _pendingPattern = "";
        UpdateGesturePatternText();
    }

    private void RemoveGestureButton_Click(object sender, RoutedEventArgs e) => RemoveSelected(GestureListBox);

    private void AddExcludeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddProcess(GestureExcludeListBox, SwitcherSettings.MaxGestureExclude, AddExcludeBox.Text))
            AddExcludeBox.Clear();
    }

    private async void CaptureExcludeButton_Click(object sender, RoutedEventArgs e) =>
        await CaptureProcessIntoAsync(CaptureExcludeButton, GestureExcludeListBox, SwitcherSettings.MaxGestureExclude);

    private void RemoveExcludeButton_Click(object sender, RoutedEventArgs e) => RemoveSelected(GestureExcludeListBox);

    /// <summary>距離の入力欄を検証する。範囲外なら null。</summary>
    private static int? ParseDistance(string text)
    {
        if (int.TryParse(text.Trim(), out int v)
            && v >= SwitcherSettings.MinGestureDistance && v <= SwitcherSettings.MaxGestureDistance)
            return v;
        return null;
    }

    // 「最小化する」OFF のときは連動オプション（タスクバーから隠す）を無効化
    private void MinimizeCheck_Changed(object sender, RoutedEventArgs e) => UpdateMinimizeSubOptions();

    private void UpdateMinimizeSubOptions()
    {
        if (HideTaskbarCheck is not null)
            HideTaskbarCheck.IsEnabled = MinimizeCheck.IsChecked == true;
    }

    // 「最前面化する」のON/OFFで、対象アプリ入力欄の有効/無効を切り替える
    private void BringToFrontCheck_Changed(object sender, RoutedEventArgs e) => UpdateTargetPanelEnabled();

    private void UpdateTargetPanelEnabled()
    {
        if (TargetPanel is not null)
            TargetPanel.IsEnabled = BringToFrontCheck.IsChecked == true;
    }

    // --- 一緒に最小化するアプリ ---
    private void AddProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddProcess(MinimizeListBox, SwitcherSettings.MaxMinimizeWith, AddProcessBox.Text))
            AddProcessBox.Clear();
    }

    private async void CaptureAddButton_Click(object sender, RoutedEventArgs e) =>
        await CaptureProcessIntoAsync(CaptureAddButton, MinimizeListBox, SwitcherSettings.MaxMinimizeWith);

    /// <summary>数秒待ってから前面ウインドウのプロセス名を取り、リストへ追加する</summary>
    private async Task CaptureProcessIntoAsync(Button button, ListBox list, int max)
    {
        using var proc = await CaptureForegroundProcessAsync(button, "秒後...");
        if (proc is not null)
            TryAddProcess(list, max, proc.ProcessName);
    }

    /// <summary>
    /// ボタンにカウントダウンを出しながら3秒待ち、その時点の前面ウインドウのプロセスを返す。
    /// 取得できなければ通知して null。戻り値は呼び出し側で破棄する。
    /// </summary>
    private async Task<Process?> CaptureForegroundProcessAsync(Button button, string countdownSuffix)
    {
        object original = button.Content;
        button.IsEnabled = false;
        try
        {
            for (int sec = 3; sec >= 1; sec--)
            {
                button.Content = $"{sec} {countdownSuffix}";
                await Task.Delay(1000);
            }

            SwNative.GetWindowThreadProcessId(SwNative.GetForegroundWindow(), out uint pid);
            try
            {
                return Process.GetProcessById((int)pid);
            }
            catch
            {
                Warn("前面ウインドウのプロセスを取得できませんでした。");
                return null;
            }
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
        }
    }

    private void RemoveProcessButton_Click(object sender, RoutedEventArgs e) => RemoveSelected(MinimizeListBox);

    // --- 閉じるタブの正規表現 ---
    private void AddCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddClosePattern(AddCloseBox.Text))
            AddCloseBox.Clear();
    }

    private void RemoveCloseButton_Click(object sender, RoutedEventArgs e) => RemoveSelected(CloseTabListBox);

    /// <summary>正規表現を検証してリストへ追加。不正・空・重複・上限を弾く。成功で true。</summary>
    private bool TryAddClosePattern(string raw)
    {
        string pat = (raw ?? "").Trim();
        if (pat.Length == 0)
            return false;

        // 正規表現として有効か確認
        try { _ = new Regex(pat); }
        catch (Exception ex)
        {
            Warn("正規表現が不正です:\n" + ex.Message);
            return false;
        }

        return TryAddUnique(CloseTabListBox, pat, SwitcherSettings.MaxCloseTabPatterns, StringComparer.Ordinal);
    }

    /// <summary>プロセス名を正規化してリストへ追加。重複（大文字小文字は無視）・空・上限を弾く。成功で true。</summary>
    private bool TryAddProcess(ListBox list, int max, string raw)
    {
        string name = ProcessHelper.Normalize(raw);
        return name.Length > 0 && TryAddUnique(list, name, max, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>重複していなければ上限の範囲でリストへ追加する。上限超過は通知する。</summary>
    private bool TryAddUnique(ListBox list, string item, int max, StringComparer comparer)
    {
        if (list.Items.Cast<string>().Contains(item, comparer))
            return false;

        if (list.Items.Count >= max)
        {
            Info($"登録できるのは最大 {max} 件までです。");
            return false;
        }

        list.Items.Add(item);
        return true;
    }

    private static void RemoveSelected(ListBox list)
    {
        if (list.SelectedItem is not null)
            list.Items.Remove(list.SelectedItem);
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "LunchBox", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Info(string message) =>
        MessageBox.Show(this, message, "LunchBox", MessageBoxButton.OK, MessageBoxImage.Information);

    // --- ホットキー入力 ---
    private void HotKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotKeyBox.Text = "キーを押してください...";
    }

    private void HotKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
    }

    private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        // 修飾キー単体ならまだ確定しない（現在押下中の修飾を表示）
        if (HotKeyText.IsModifierKey(key))
        {
            HotKeyBox.Text = HotKeyText.WithSeparator(
                HotKeyText.FormatModifiers(HotKeyText.FromWpfModifiers(Keyboard.Modifiers))) + "...";
            return;
        }

        // Esc は入力キャンセル
        if (key == Key.Escape)
        {
            HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
            return;
        }

        var (mods, vk) = HotKeyText.FromWpf(key, Keyboard.Modifiers);
        if (vk == 0)
            return;

        _pendingModifiers = mods;
        _pendingVk = vk;
        HotKeyBox.Text = HotKeyText.Format(mods, vk);
    }

    // --- 前面ウインドウからプロセス名（と exe パス）を取得 ---
    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        using var proc = await CaptureForegroundProcessAsync(CaptureButton, "秒後に取得...");
        if (proc is null) return;

        ProcessNameBox.Text = proc.ProcessName;
        try
        {
            string? path = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
                ExePathBox.Text = path;
        }
        catch
        {
            // 権限不足等で取得できない場合はプロセス名のみ
        }
    }

    // --- exe 参照 ---
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "対象アプリの実行ファイルを選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(ExePathBox.Text))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(ExePathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == true)
        {
            ExePathBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(ProcessNameBox.Text))
                ProcessNameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    // --- 保存 / キャンセル ---
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingVk == 0)
        {
            Warn("ショートカットキーを設定してください。");
            return;
        }
        bool bringToFront = BringToFrontCheck.IsChecked == true;
        // 対象アプリの指定が要るのは「最前面化する」場合だけ
        if (bringToFront
            && string.IsNullOrWhiteSpace(ProcessNameBox.Text)
            && string.IsNullOrWhiteSpace(ExePathBox.Text))
        {
            Warn("「対象アプリを最前面に出す」が ON です。対象アプリ（プロセス名または exe パス）を設定してください。");
            return;
        }

        int? startDist = ParseDistance(GestureStartBox.Text);
        int? strokeDist = ParseDistance(GestureStrokeBox.Text);
        if (startDist is null || strokeDist is null)
        {
            Warn($"マウスジェスチャの判定距離は {SwitcherSettings.MinGestureDistance}〜{SwitcherSettings.MaxGestureDistance} の整数で入力してください。");
            return;
        }
        bool gestureOn = GestureEnableCheck.IsChecked == true;
        if (gestureOn && GestureListBox.Items.Count == 0)
        {
            Warn("「マウスジェスチャを使う」が ON です。ジェスチャを1件以上登録してください。");
            return;
        }

        _settings.Modifiers = _pendingModifiers;
        _settings.VirtualKey = _pendingVk;
        _settings.BringTargetToFront = bringToFront;
        _settings.TargetProcessName = ProcessNameBox.Text.Trim();
        _settings.TargetExePath = ExePathBox.Text.Trim();
        _settings.MinimizeCurrent = MinimizeCheck.IsChecked == true;
        _settings.HideFromTaskbar = HideTaskbarCheck.IsChecked == true;
        _settings.StartWithWindows = StartupCheck.IsChecked == true;
        _settings.MinimizeWithProcesses = MinimizeListBox.Items.Cast<string>().ToList();
        _settings.CloseTabPatterns = CloseTabListBox.Items.Cast<string>().ToList();
        _settings.MouseGestureEnabled = gestureOn;
        _settings.MouseGestures = GestureListBox.Items.Cast<GestureBinding>().Select(g => g.Clone()).ToList();
        _settings.GestureStartDistance = startDist.Value;
        _settings.GestureStrokeDistance = strokeDist.Value;
        _settings.GestureExcludeProcesses = GestureExcludeListBox.Items.Cast<string>().ToList();

        SettingsSaved?.Invoke(_settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
