using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MenuItem = System.Windows.Controls.MenuItem;

namespace LunchBox;

/// <summary>
/// 履歴（クリップボード／アプリの「最近」）を全件、絞り込み付きで見るウインドウ。
/// 数千件でも軽いように ListView の仮想化を使い、アイコンは行が見えたときに作る。
/// </summary>
public partial class HistoryBrowserWindow : Window
{
    /// <summary>一覧の1行。アイコンは初めて要求されたときに作る</summary>
    public sealed class Row
    {
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public DateTime When { get; init; }
        public string WhenText => When == DateTime.MinValue ? "" : When.ToString("yyyy/MM/dd HH:mm");
        public object? Source { get; init; }
        public Func<ImageSource?>? IconLoader { get; init; }
        private ImageSource? _icon;
        private bool _iconLoaded;
        public ImageSource? Icon
        {
            get
            {
                if (!_iconLoaded)
                {
                    _iconLoaded = true;
                    try { _icon = IconLoader?.Invoke(); } catch { _icon = null; }
                }
                return _icon;
            }
        }
    }

    public sealed record RowAction(string Text, Action<Row> Run, bool RefreshAfter);

    private List<Row> _all;
    private readonly Func<List<Row>> _reload;
    private readonly Action<Row> _primary;
    private readonly List<RowAction> _actions;

    /// <param name="title">ウインドウ名</param>
    /// <param name="reload">全行を取り直す関数（削除などの後に呼ぶ）</param>
    /// <param name="primaryText">実行ボタンの文言（開く／コピー）</param>
    /// <param name="primary">ダブルクリック・Enter の動作</param>
    /// <param name="actions">右クリックの操作</param>
    public HistoryBrowserWindow(string title, Func<List<Row>> reload, string primaryText, Action<Row> primary, List<RowAction> actions)
    {
        InitializeComponent();
        Title = title;
        _reload = reload;
        _primary = primary;
        _actions = actions;
        PrimaryButton.Content = primaryText;
        _all = reload();
        ApplyFilter();
        Loaded += (_, _) => FilterBox.Focus();
    }

    public void Refresh()
    {
        _all = _reload();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim();
        var sel = List.SelectedItem;
        List<Row> rows;
        if (f.Length == 0) rows = _all;
        else
        {
            // 空白区切りの AND 検索
            var words = f.Split(' ', '　').Where(w => w.Length > 0).ToArray();
            rows = _all.Where(r => words.All(w =>
                r.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                r.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
        }
        List.ItemsSource = rows;
        CountText.Text = f.Length == 0 ? $"{rows.Count} 件" : $"{rows.Count} / {_all.Count} 件";
        if (sel is Row s && rows.Contains(s)) List.SelectedItem = s;
        else if (rows.Count > 0) List.SelectedIndex = 0;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void RunPrimary()
    {
        if (List.SelectedItem is not Row r) return;
        try { _primary(r); }
        catch (Exception ex) { MessageBox.Show(ex.Message, AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RunPrimary();
    private void Primary_Click(object sender, RoutedEventArgs e) => RunPrimary();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunPrimary(); e.Handled = true; }
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();
        if (List.SelectedItem is not Row r) { e.Handled = true; return; }
        foreach (var a in _actions)
        {
            var act = a;
            var mi = new MenuItem { Header = act.Text };
            mi.Click += (_, _) =>
            {
                try { act.Run(r); }
                catch (Exception ex) { MessageBox.Show(ex.Message, AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning); }
                if (act.RefreshAfter) Refresh();
            };
            RowMenu.Items.Add(mi);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
