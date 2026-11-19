using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using MessageBox = System.Windows.MessageBox;

namespace LunchBox;

/// <summary>ピン留め1件の編集ダイアログ</summary>
public partial class EditItemWindow : Window
{
    private readonly PinItem _pin;
    private bool _kindTouched;
    private bool _loading;

    /// <summary>OK で閉じたときの結果</summary>
    public PinItem? Result { get; private set; }

    public EditItemWindow(PinItem pin, string appName)
    {
        InitializeComponent();
        _pin = pin;
        Title = $"ピン留めの編集 - {appName}";

        _loading = true;
        LabelBox.Text = pin.Label;
        KindBox.SelectedIndex = (int)pin.Kind;
        TargetBox.Text = pin.Target;
        ArgsBox.Text = pin.Arguments ?? "";
        MemoBox.Text = pin.Memo ?? "";
        _kindTouched = !string.IsNullOrEmpty(pin.Target);
        _loading = false;

        UpdateBrowseState();
        UpdateIconPreview();
        UpdateStatusText();
        LabelBox.Focus();
    }

    private ItemKind SelectedKind => (ItemKind)Math.Max(0, KindBox.SelectedIndex);

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _kindTouched = true;
        UpdateBrowseState();
        UpdateIconPreview();
    }

    private void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!_kindTouched)
        {
            _loading = true;
            KindBox.SelectedIndex = (int)PinItem.GuessKind(TargetBox.Text);
            _loading = false;
            UpdateBrowseState();
        }
        UpdateIconPreview();
    }

    private void UpdateBrowseState()
    {
        BrowseButton.IsEnabled = SelectedKind != ItemKind.Url;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKind == ItemKind.Folder)
        {
            var dlg = new OpenFolderDialog { Title = "フォルダを選択" };
            try
            {
                if (Directory.Exists(TargetBox.Text)) dlg.InitialDirectory = TargetBox.Text;
            }
            catch { }
            if (dlg.ShowDialog(this) == true)
                TargetBox.Text = dlg.FolderName;
        }
        else
        {
            var dlg = new OpenFileDialog { Title = "ファイルを選択", CheckFileExists = false };
            try
            {
                string? dir = Path.GetDirectoryName(TargetBox.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                dlg.FileName = Path.GetFileName(TargetBox.Text);
            }
            catch { }
            if (dlg.ShowDialog(this) == true)
                TargetBox.Text = dlg.FileName;
        }
    }

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "アイコンを選択",
            Filter = "アイコン・画像・実行ファイル|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _pin.IconPath = dlg.FileName;
            IconCache.Invalidate(dlg.FileName);
            UpdateIconPreview();
        }
    }

    private void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        _pin.IconPath = null;
        if (SelectedKind == ItemKind.Url)
            IconCache.RefreshFavicon(TargetBox.Text.Trim());
        UpdateIconPreview();
    }

    private void UpdateIconPreview()
    {
        var temp = new PinItem { Kind = SelectedKind, Target = TargetBox.Text.Trim(), IconPath = _pin.IconPath, TargetUnc = _pin.TargetUnc };
        IconPreview.Source = IconCache.ToBitmapSource(IconCache.ForPin(temp));
        IconPathText.Text = string.IsNullOrEmpty(_pin.IconPath) ? "（自動）" : Path.GetFileName(_pin.IconPath);
    }

    private void UpdateStatusText()
    {
        string s = _pin.Status switch
        {
            LinkStatus.Ok => "見つかります",
            LinkStatus.Relinked => "移動先を自動で追跡しました",
            LinkStatus.NotFound => "⚠ 見つかりません",
            _ => "未確認",
        };
        if (_pin.LastChecked is DateTime t)
            s += $"（最終確認 {t:yyyy/MM/dd HH:mm}）";
        StatusText.Text = s;
        StatusText.Foreground = _pin.Status == LinkStatus.NotFound
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Black;
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyToPin(validate: true)) return;
        IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await Task.Run(() =>
            {
                LinkResolver.CaptureIdentity(_pin);
                if (_pin.Status == LinkStatus.NotFound)
                    LinkResolver.Check(_pin);
            });
            _loading = true;
            TargetBox.Text = _pin.Target; // 追跡で変わった場合
            _loading = false;
        }
        finally
        {
            Cursor = null;
            IsEnabled = true;
        }
        UpdateStatusText();
        UpdateIconPreview();
    }

    /// <summary>画面の内容を _pin に反映。検証エラーなら false</summary>
    private bool ApplyToPin(bool validate)
    {
        string target = TargetBox.Text.Trim();
        if (validate && string.IsNullOrEmpty(target))
        {
            MessageBox.Show("ショートカット先を入力してください。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            TargetBox.Focus();
            return false;
        }
        var kind = SelectedKind;
        if (kind == ItemKind.Url && validate && !Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            MessageBox.Show("URL の形式が正しくありません。", AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            TargetBox.Focus();
            return false;
        }

        bool targetChanged = !string.Equals(_pin.Target, target, StringComparison.OrdinalIgnoreCase) || _pin.Kind != kind;
        _pin.Label = LabelBox.Text.Trim();
        _pin.Kind = kind;
        _pin.Target = target;
        _pin.Arguments = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim();
        _pin.Memo = string.IsNullOrWhiteSpace(MemoBox.Text) ? null : MemoBox.Text.Trim();
        if (targetChanged)
        {
            _pin.TargetUnc = null;
            _pin.FileId = _pin.VolumeId = _pin.VolumeRoot = null;
            _pin.Status = LinkStatus.Unknown;
        }
        return true;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyToPin(validate: true)) return;
        if (string.IsNullOrEmpty(_pin.FileId) || _pin.Status == LinkStatus.Unknown)
        {
            IsEnabled = false;
            Cursor = System.Windows.Input.Cursors.Wait;
            try { await Task.Run(() => LinkResolver.CaptureIdentity(_pin)); }
            finally { Cursor = null; IsEnabled = true; }
        }
        Result = _pin;
        DialogResult = true;
    }
}
