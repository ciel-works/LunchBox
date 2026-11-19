using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LunchBox;

/// <summary>Windows の「すべてのアプリ」から選んで登録するダイアログ</summary>
public partial class AppPickerWindow : Window
{
    public sealed class Row
    {
        public ShellApps.Entry Entry { get; }
        public string Name => Entry.Name;
        public string Kind => Entry.IsPackaged ? "ストアアプリ" : "";
        public ImageSource? Icon { get; set; }
        public Row(ShellApps.Entry e) { Entry = e; }
    }

    private readonly List<Row> _all = new();
    private readonly ObservableCollection<Row> _view = new();

    /// <summary>「追加」で閉じたときの選択</summary>
    public ShellApps.Entry? Selected { get; private set; }

    public AppPickerWindow()
    {
        InitializeComponent();
        AppList.ItemsSource = _view;
        Loaded += async (_, _) =>
        {
            CountText.Text = "読み込み中...";
            var entries = await Task.Run(ShellApps.Enumerate);
            foreach (var e in entries) _all.Add(new Row(e));
            ApplyFilter();
            FilterBox.Focus();
            // アイコンは後からバックグラウンドで
            _ = Task.Run(() =>
            {
                foreach (var r in _all.ToList())
                {
                    // 変換が済んだら元の画像はすぐ手放す（アプリの数だけ溜めない）
                    using var bmp = ShellApps.GetIcon(r.Entry.LaunchTarget, 32);
                    if (bmp is null) continue;
                    var src = IconCache.ToBitmapSource(bmp);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        r.Icon = src;
                        int i = _view.IndexOf(r);
                        if (i >= 0) { _view.RemoveAt(i); _view.Insert(i, r); }
                    }));
                }
            });
        };
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim();
        var sel = AppList.SelectedItem;
        _view.Clear();
        foreach (var r in _all)
        {
            if (f.Length == 0 || r.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                r.Entry.Path.Contains(f, StringComparison.OrdinalIgnoreCase))
                _view.Add(r);
        }
        CountText.Text = $"{_view.Count} / {_all.Count} 件";
        if (sel is Row s && _view.Contains(s)) AppList.SelectedItem = s;
        else if (_view.Count > 0) AppList.SelectedIndex = 0;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem is Row) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not Row r) return;
        Selected = r.Entry;
        DialogResult = true;
    }
}
