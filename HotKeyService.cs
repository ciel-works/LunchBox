using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LunchBox;

/// <summary>
/// グローバルホットキーを登録し、押下時にイベントを発火する。
/// メッセージ受信用に不可視の HwndSource を内部生成する。（FrontSwitcher から流用）
/// </summary>
public sealed class HotKeyService : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int WM_HOTKEY = 0x0312;
    private readonly int HOTKEY_ID;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? HotKeyPressed;

    /// <param name="id">ホットキーの識別子（同じアプリ内で複数使うときは別の値にする）</param>
    public HotKeyService(int id = 0xB002)
    {
        HOTKEY_ID = id;
        var parameters = new HwndSourceParameters("LunchBoxHotKeyWindow" + id)
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        if (virtualKey == 0)
            return false;

        _registered = RegisterHotKey(_source.Handle, HOTKEY_ID, modifiers | MOD_NOREPEAT, virtualKey);
        int err = _registered ? 0 : Marshal.GetLastWin32Error();
        Logger.Log($"RegisterHotKey mod={modifiers} vk={virtualKey} -> {_registered} (err={err})");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotKeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
