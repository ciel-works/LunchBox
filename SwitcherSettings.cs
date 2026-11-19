namespace LunchBox;

/// <summary>
/// ウインドウ切替（旧 FrontSwitcher）の設定。LunchBox の settings.json の Switcher に入る。
/// プロパティ名は FrontSwitcher の settings.json と同じ（そのまま読み込んで引き継げる）。
/// </summary>
public sealed class SwitcherSettings
{
    // --- ホットキー ---
    /// <summary>Win32 修飾キー（MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8 の組み合わせ）</summary>
    public uint Modifiers { get; set; } = HotKeyService.MOD_CONTROL | HotKeyService.MOD_ALT;

    /// <summary>仮想キーコード（VK_*）。既定は Z (0x5A)</summary>
    public uint VirtualKey { get; set; } = 0x5A;

    // --- 対象アプリ ---
    public string TargetProcessName { get; set; } = "";
    public string TargetExePath { get; set; } = "";

    // --- 動作オプション ---
    /// <summary>対象アプリを最前面に出すか。false（既定）ならホットキーで「最小化のみ」</summary>
    public bool BringTargetToFront { get; set; } = false;
    public bool MinimizeCurrent { get; set; } = true;
    public bool HideFromTaskbar { get; set; } = false;

    /// <summary>一緒に最小化するアプリのプロセス名（拡張子なし）。最大 20 件</summary>
    public List<string> MinimizeWithProcesses { get; set; } = new();
    public const int MaxMinimizeWith = 20;

    /// <summary>ホットキー時に閉じるブラウザタブのタイトル正規表現。最大 20 件</summary>
    public List<string> CloseTabPatterns { get; set; } = new();
    public const int MaxCloseTabPatterns = 20;

    // --- マウスジェスチャ ---
    public bool MouseGestureEnabled { get; set; } = false;
    public List<GestureBinding> MouseGestures { get; set; } = GestureBinding.Defaults();
    public const int MaxGestures = 10;
    public int GestureStartDistance { get; set; } = 20;
    public int GestureStrokeDistance { get; set; } = 30;
    public const int MinGestureDistance = 5;
    public const int MaxGestureDistance = 300;
    public List<string> GestureExcludeProcesses { get; set; } = new();
    public const int MaxGestureExclude = 20;

    /// <summary>FrontSwitcher 時代の項目（LunchBox では使わない。自動起動は LunchBox 側の設定）</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>壊れた値を補正する（null のリスト、範囲外の距離、不正な並び）</summary>
    public void Normalize()
    {
        MinimizeWithProcesses ??= new();
        CloseTabPatterns ??= new();
        GestureExcludeProcesses ??= new();
        MouseGestures ??= new();
        MouseGestures.RemoveAll(g => g is null || !GestureBinding.IsValidPattern(g.Pattern));
        GestureStartDistance = Math.Clamp(GestureStartDistance, MinGestureDistance, MaxGestureDistance);
        GestureStrokeDistance = Math.Clamp(GestureStrokeDistance, MinGestureDistance, MaxGestureDistance);
    }

    public SwitcherSettings Clone()
    {
        var copy = (SwitcherSettings)MemberwiseClone();
        copy.MinimizeWithProcesses = new List<string>(MinimizeWithProcesses);
        copy.CloseTabPatterns = new List<string>(CloseTabPatterns);
        copy.MouseGestures = MouseGestures.Select(g => g.Clone()).ToList();
        copy.GestureExcludeProcesses = new List<string>(GestureExcludeProcesses);
        return copy;
    }
}
