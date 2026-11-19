using System.Diagnostics;
using System.IO;

namespace LunchBox;

/// <summary>
/// ウインドウの切替ロジック本体。ホットキーの動作は設定で2通り。
/// ・最小化のみ(既定): 隠すべきものが見えていれば隠す／何も見えていなければ隠したものを全部戻す
/// ・最前面化(BringTargetToFront=true): 最小化＋対象アプリを前面（未起動なら起動）／対象が前面なら元へ戻す
/// マウスジェスチャからは「隠す」「戻す」「対象アプリを前面へ」を単独でも呼ぶ。
/// </summary>
public sealed class WindowSwitcher
{
    /// <summary>退避（最小化）したウインドウ一覧。元前面ウインドウ＋一緒に最小化した登録アプリ。</summary>
    private readonly List<IntPtr> _stashed = new();

    /// <summary>元の前面ウインドウ（復元時に最前面へ戻す）。</summary>
    private IntPtr _stashedPrimary = IntPtr.Zero;

    /// <summary>ホットキー押下時に呼ばれる。設定に従って切替を行う。</summary>
    public Task SwitchAsync(SwitcherSettings settings) =>
        settings.BringTargetToFront ? SwitchBringToFrontAsync(settings) : SwitchMinimizeOnlyAsync(settings);

    /// <summary>
    /// 最小化のみモード。判定規則は一つ：
    /// 「隠すべきものが画面に見えているなら隠す。何も見えていなければ、隠したものを全部戻す」。
    /// ・隠すべきもの＝登録アプリの表示中ウインドウ、または手動復元された退避済みウインドウ
    /// ・登録アプリはウインドウを開き直すと識別子が変わるため、リスト照合でなく
    ///   「登録プロセスの表示中ウインドウが存在するか」で判定する
    /// ・何も隠しておらず登録アプリも見えていなければ、前面ウインドウを新規に最小化する
    /// </summary>
    private async Task SwitchMinimizeOnlyAsync(SwitcherSettings settings)
    {
        IntPtr foreground = SwNative.GetForegroundWindow();

        // 登録アプリの「表示中（最小化されていない）」ウインドウ
        var visibleTargets = FindVisibleRegisteredWindows(settings);

        // 退避済みのウインドウをユーザーが手動で復元して前面で使っているか
        bool fgIsRestoredStash = foreground != IntPtr.Zero
            && _stashed.Contains(foreground)
            && !SwNative.IsIconic(foreground);

        if (visibleTargets.Count == 0 && !fgIsRestoredStash && IsStashActive())
        {
            // 隠すべきものは無く、隠したものが残っている → まとめて復元
            Logger.Log("  -> 復元(トグル)");
            RestoreStashedWindow();
            return;
        }

        // 既存の退避記憶は消さず追記する
        Logger.Log($"  -> 最小化パス (登録アプリ表示中={visibleTargets.Count} 前面が退避済み={fgIsRestoredStash})");
        await HideCoreAsync(settings, IntPtr.Zero, visibleTargets);
    }

    /// <summary>最前面化モード（従来動作）。</summary>
    private async Task SwitchBringToFrontAsync(SwitcherSettings settings)
    {
        IntPtr foreground = SwNative.GetForegroundWindow();
        IntPtr target = FindTargetWindow(settings);

        // 対象が既に前面にある（=直前に切替済み）なら、対象を引っ込めて退避ウインドウを元に戻す
        if (target != IntPtr.Zero && foreground == target)
        {
            if (settings.MinimizeCurrent && SwNative.IsWindow(target))
                SwNative.ShowWindow(target, SwNative.SW_MINIMIZE);
            RestoreStashedWindow();
            return;
        }

        if (target == IntPtr.Zero)
        {
            target = await LaunchTargetAsync(settings);
            if (target == IntPtr.Zero) return;
        }

        // 前面ウインドウ＋登録アプリを最小化してから対象を前面化
        Minimize(settings, target);
        BringToFront(target);
        await CloseTabsAsync(settings);
    }

    // ===== マウスジェスチャから単独で呼ぶ動作 =====

    /// <summary>トグルの「隠す」側だけを必ず実行する（前面ウインドウ＋登録アプリを最小化、タブも閉じる）。</summary>
    public Task HideAsync(SwitcherSettings settings)
    {
        Logger.Log("  -> 隠す（ジェスチャ）");
        return HideCoreAsync(settings, IntPtr.Zero);
    }

    /// <summary>対象アプリを前面に出す（未起動なら起動）。他のウインドウは最小化しない。</summary>
    public async Task BringTargetAsync(SwitcherSettings settings)
    {
        IntPtr target = FindTargetWindow(settings);
        if (target == IntPtr.Zero)
            target = await LaunchTargetAsync(settings);
        if (target != IntPtr.Zero)
            BringToFront(target);
    }

    /// <summary>最小化してからタブを閉じる（ホットキー・ジェスチャ共通の「隠す」）</summary>
    private async Task HideCoreAsync(SwitcherSettings settings, IntPtr excludeTarget, List<IntPtr>? visibleTargets = null)
    {
        Minimize(settings, excludeTarget, visibleTargets);
        await CloseTabsAsync(settings);
    }

    /// <summary>未起動の対象アプリを起動してウインドウを待つ。失敗時は通知して Zero を返す。</summary>
    private static async Task<IntPtr> LaunchTargetAsync(SwitcherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(ResolveProcessName(settings)))
        {
            ShowTrayBalloon("対象アプリが設定されていません。設定の「最前面化」タブで指定してください。");
            return IntPtr.Zero;
        }
        IntPtr target = await LaunchAndWaitAsync(settings);
        if (target == IntPtr.Zero)
            ShowTrayBalloon("対象アプリのウインドウを見つけられませんでした。設定を確認してください。");
        return target;
    }

    /// <summary>
    /// 一般的な窓操作（最小化・最大化・閉じる）。ユーザーがタイトルバーのボタンを押したのと同じ
    /// WM_SYSCOMMAND を送る（アプリ側の保存確認などはそのまま働く）。
    /// デスクトップやタスクバーは対象外（閉じるとシャットダウン画面が出るため）。
    /// </summary>
    public static void WindowCommand(IntPtr hWnd, GestureAction action)
    {
        if (hWnd == IntPtr.Zero || !SwNative.IsWindow(hWnd)) return;
        string cls = SwNative.GetClassNameOf(hWnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            Logger.Log($"  窓操作スキップ（シェルのウインドウ: {cls}）");
            return;
        }

        int cmd = action switch
        {
            GestureAction.MinimizeWindow => SwNative.SC_MINIMIZE,
            GestureAction.ToggleMaximizeWindow => SwNative.IsZoomed(hWnd) ? SwNative.SC_RESTORE : SwNative.SC_MAXIMIZE,
            GestureAction.CloseWindow => SwNative.SC_CLOSE,
            _ => 0,
        };
        if (cmd == 0) return;
        SwNative.PostMessage(hWnd, SwNative.WM_SYSCOMMAND, new IntPtr(cmd), IntPtr.Zero);
        Logger.Log($"  窓操作 {action} hWnd={hWnd} class={cls}");
    }

    /// <summary>設定された正規表現にマッチするブラウザタブを閉じる（Chrome/Edge）。</summary>
    private async Task CloseTabsAsync(SwitcherSettings settings)
    {
        if (settings.CloseTabPatterns is null || settings.CloseTabPatterns.Count == 0)
            return;
        try
        {
            // UI Automation は時間がかかるためバックグラウンドで実行（UIを固めない）
            int n = await Task.Run(() => BrowserTabCloser.CloseMatchingTabs(settings.CloseTabPatterns));
            if (n > 0)
                ShowTrayBalloon($"一致したタブを {n} 個閉じました。");
        }
        catch (Exception ex)
        {
            Logger.Log("CloseTabsAsync error: " + ex.Message);
        }
    }

    /// <summary>
    /// 前面ウインドウと「一緒に最小化するアプリ」を最小化して退避する。
    /// 既存の退避記憶は消さず追記する（連続で隠しても、復元で全部戻せるように）。
    /// excludeTarget は最小化対象から除外する（最前面化モードの対象アプリ自身）。
    /// visibleTargets を渡すと登録アプリの再列挙を省略する。
    /// </summary>
    private void Minimize(SwitcherSettings settings, IntPtr excludeTarget, List<IntPtr>? visibleTargets = null)
    {
        // 閉じられた等で無効になったハンドルだけ掃除する（記憶自体は保持）
        _stashed.RemoveAll(h => !SwNative.IsWindow(h));
        if (_stashedPrimary != IntPtr.Zero && !SwNative.IsWindow(_stashedPrimary))
            _stashedPrimary = IntPtr.Zero;

        // 元の前面ウインドウ
        IntPtr foreground = SwNative.GetForegroundWindow();
        Logger.Log($"  Minimize: fg={foreground} isWin={SwNative.IsWindow(foreground)} minCur={settings.MinimizeCurrent} with=[{string.Join(",", settings.MinimizeWithProcesses)}]");
        if (settings.MinimizeCurrent
            && foreground != IntPtr.Zero
            && foreground != excludeTarget
            && SwNative.IsWindow(foreground)
            && !SwNative.IsIconic(foreground))
        {
            MinimizeOne(foreground, settings);
            _stashedPrimary = foreground;
            if (!_stashed.Contains(foreground))
                _stashed.Add(foreground);
        }

        // 一緒に最小化する登録アプリ（表示中のウインドウだけ対象）
        visibleTargets ??= FindVisibleRegisteredWindows(settings);
        int added = 0;
        foreach (IntPtr hwnd in visibleTargets)
        {
            if (hwnd == excludeTarget) continue;
            if (SwNative.IsIconic(hwnd)) continue; // 念のため（表示中リストのはず）
            MinimizeOne(hwnd, settings);
            if (!_stashed.Contains(hwnd))
            {
                _stashed.Add(hwnd);
                added++;
            }
        }

        Logger.Log($"  -> 最小化実行 (退避合計={_stashed.Count}窓, 今回登録アプリ={added}窓, 前面含む={_stashedPrimary != IntPtr.Zero})");
    }

    /// <summary>登録アプリの「表示中（最小化されていない）」トップレベルウインドウを列挙する</summary>
    private static List<IntPtr> FindVisibleRegisteredWindows(SwitcherSettings settings) =>
        FindWindowsOfProcesses(settings.MinimizeWithProcesses)
            .Where(h => !SwNative.IsIconic(h))
            .ToList();

    private static void MinimizeOne(IntPtr hWnd, SwitcherSettings settings)
    {
        SwNative.ShowWindow(hWnd, SwNative.SW_MINIMIZE);
        // 一部のアプリは通常の最小化を無視するため、強制最小化も試みる
        if (!SwNative.IsIconic(hWnd))
            SwNative.ShowWindow(hWnd, SwNative.SW_FORCEMINIMIZE);

        // 実際に最小化できたときだけタスクバーボタンを消す
        // （最小化に失敗したのにボタンだけ消える不整合を防ぐ）
        if (settings.HideFromTaskbar && SwNative.IsIconic(hWnd))
            TaskbarButton.Hide(hWnd);
        else if (settings.HideFromTaskbar)
            Logger.Log($"最小化できなかったためタスクバー非表示をスキップ: hWnd={hWnd}");
    }

    /// <summary>退避ウインドウのうち、まだ最小化中のものがあるか（トグル判定用）</summary>
    private bool IsStashActive()
    {
        foreach (IntPtr h in _stashed)
            if (SwNative.IsWindow(h) && SwNative.IsIconic(h))
                return true;
        return false;
    }

    /// <summary>
    /// 退避していたウインドウをまとめて元に戻す（消していたタスクバーボタンも復活）。
    /// 最後に「元の前面ウインドウ」を最前面にする。ホットキーのトグルからも、
    /// トレイメニュー「隠したウインドウを戻す」からも使う。戻すものが無ければ false。
    /// </summary>
    public bool RestoreStashedWindow()
    {
        bool restored = false;
        IntPtr lastRestored = IntPtr.Zero;

        // 元前面以外（一緒に最小化した登録アプリ）を先に復元
        foreach (IntPtr h in _stashed)
        {
            if (h == _stashedPrimary) continue;
            if (SwNative.IsWindow(h))
            {
                TaskbarButton.Show(h);
                if (SwNative.IsIconic(h))
                {
                    SwNative.ShowWindow(h, SwNative.SW_RESTORE);
                    lastRestored = h;
                }
                restored = true;
            }
        }

        // 最後に元の前面ウインドウを最前面へ。
        // 前面の記憶が無い場合（アクティブ最小化OFFの構成）は、復元した登録アプリを
        // 前面に出す（これが無いと、戻ったのに背後に隠れて「戻らない」ように見える）。
        if (_stashedPrimary != IntPtr.Zero && SwNative.IsWindow(_stashedPrimary))
        {
            TaskbarButton.Show(_stashedPrimary);
            BringToFront(_stashedPrimary);
            restored = true;
        }
        else if (lastRestored != IntPtr.Zero)
        {
            BringToFront(lastRestored);
        }

        _stashed.Clear();
        _stashedPrimary = IntPtr.Zero;
        return restored;
    }

    /// <summary>
    /// 指定プロセス群の、最小化対象となるトップレベル可視ウインドウを列挙する。
    /// ウインドウの総なめ（EnumWindows）はプロセスの数によらず1回で済ませる。
    /// 結果は登録リストの順に並べる（復元時にどれを前面に出すかがこの順で決まるため）。
    /// </summary>
    private static List<IntPtr> FindWindowsOfProcesses(IEnumerable<string> processNames)
    {
        var found = new List<(IntPtr hwnd, int order)>();
        var pidOrder = new Dictionary<uint, int>(); // PID → 登録リスト上の位置
        int index = 0;
        foreach (string name in processNames)
        {
            foreach (int id in ProcessHelper.SelectByName(name, p => p.Id))
                pidOrder.TryAdd((uint)id, index);
            index++;
        }
        if (pidOrder.Count == 0) return new List<IntPtr>();

        SwNative.EnumWindows((hwnd, _) =>
        {
            if (!SwNative.IsWindowVisible(hwnd)) return true;
            SwNative.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pidOrder.TryGetValue(pid, out int order)) return true;
            if (SwNative.GetWindow(hwnd, SwNative.GW_OWNER) != IntPtr.Zero) return true; // 所有される子は除外
            long ex = SwNative.GetWindowLongValue(hwnd, SwNative.GWL_EXSTYLE);
            if ((ex & SwNative.WS_EX_TOOLWINDOW) != 0) return true; // ツールウインドウ除外
            if (SwNative.GetWindowTextLength(hwnd) == 0) return true; // タイトル無しは除外
            found.Add((hwnd, order));
            return true;
        }, IntPtr.Zero);

        // OrderBy は安定ソートなので、同じアプリ内の順序（前面側から）は保たれる
        return found.OrderBy(f => f.order).Select(f => f.hwnd).ToList();
    }

    /// <summary>設定のプロセス名（無ければ exe 名）で対象ウインドウを探す</summary>
    private static IntPtr FindTargetWindow(SwitcherSettings settings)
    {
        return ProcessHelper.MainWindowsByName(ResolveProcessName(settings))
            .FirstOrDefault(SwNative.IsWindow);
    }

    private static string ResolveProcessName(SwitcherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TargetProcessName))
            return ProcessHelper.Normalize(settings.TargetProcessName);
        if (!string.IsNullOrWhiteSpace(settings.TargetExePath))
            return Path.GetFileNameWithoutExtension(settings.TargetExePath);
        return "";
    }

    /// <summary>exe を起動し、ウインドウが現れるまで最大 ~6 秒待つ</summary>
    private static async Task<IntPtr> LaunchAndWaitAsync(SwitcherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TargetExePath) || !File.Exists(settings.TargetExePath))
            return IntPtr.Zero;

        try
        {
            var psi = new ProcessStartInfo(settings.TargetExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(settings.TargetExePath) ?? "",
            };
            Process.Start(psi);
        }
        catch
        {
            return IntPtr.Zero;
        }

        // プロセス名でポーリング（ブラウザ等は別プロセスに引き継ぐため名前で探す方が確実）
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(100);
            IntPtr h = FindTargetWindow(settings);
            if (h != IntPtr.Zero)
                return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>SetForegroundWindow の制約を回避しつつ確実に前面化する</summary>
    private static void BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !SwNative.IsWindow(hWnd))
            return;

        if (SwNative.IsIconic(hWnd))
            SwNative.ShowWindow(hWnd, SwNative.SW_RESTORE);
        else
            SwNative.ShowWindow(hWnd, SwNative.SW_SHOW);

        // フォアグラウンドスレッドに入力を一時アタッチして前面化を許可させる
        uint foreThread = SwNative.GetWindowThreadProcessId(SwNative.GetForegroundWindow(), out _);
        uint targetThread = SwNative.GetWindowThreadProcessId(hWnd, out _);
        uint currentThread = SwNative.GetCurrentThreadId();

        bool attached1 = false, attached2 = false;
        try
        {
            if (foreThread != currentThread)
                attached1 = SwNative.AttachThreadInput(currentThread, foreThread, true);
            if (targetThread != currentThread && targetThread != foreThread)
                attached2 = SwNative.AttachThreadInput(currentThread, targetThread, true);

            SwNative.BringWindowToTop(hWnd);
            SwNative.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached1) SwNative.AttachThreadInput(currentThread, foreThread, false);
            if (attached2) SwNative.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private static void ShowTrayBalloon(string message)
    {
        TrayBalloonRequested?.Invoke(message);
    }

    /// <summary>トレイ通知を出したいときに App 側で購読する</summary>
    public static event Action<string>? TrayBalloonRequested;
}
