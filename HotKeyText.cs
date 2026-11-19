using System.Windows.Input;

namespace LunchBox;

/// <summary>ホットキーの修飾キー・仮想キーを人間が読める文字列に変換する</summary>
internal static class HotKeyText
{
    public static string Format(uint modifiers, uint virtualKey)
    {
        if (virtualKey == 0)
            return "(未設定)";
        return WithSeparator(FormatModifiers(modifiers)) + KeyName(virtualKey);
    }

    /// <summary>日本語キーボード向けの読みやすいキー名</summary>
    public static string KeyName(uint virtualKey)
    {
        switch (virtualKey)
        {
            case 0x1D: return "無変換";
            case 0x1C: return "変換";
            case 0x15: return "カタカナ/ひらがな";
            case 0xF3:
            case 0xF4: return "半角/全角";
            case 0x20: return "Space";
            case 0x2E: return "Delete";
            case 0x2D: return "Insert";
            case 0x24: return "Home";
            case 0x23: return "End";
            case 0x21: return "PageUp";
            case 0x22: return "PageDown";
        }
        Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        if (key == Key.None) return $"VK{virtualKey:X2}";
        string s = key.ToString();
        if (s.StartsWith("D") && s.Length == 2 && char.IsDigit(s[1])) return s.Substring(1);
        return s;
    }

    public static string FormatModifiers(uint modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & HotKeyService.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & HotKeyService.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & HotKeyService.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & HotKeyService.MOD_WIN) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    public static string WithSeparator(string modifiersText) =>
        modifiersText.Length > 0 ? modifiersText + " + " : "";

    public static uint FromWpfModifiers(ModifierKeys mods)
    {
        uint m = 0;
        if ((mods & ModifierKeys.Control) != 0) m |= HotKeyService.MOD_CONTROL;
        if ((mods & ModifierKeys.Alt) != 0) m |= HotKeyService.MOD_ALT;
        if ((mods & ModifierKeys.Shift) != 0) m |= HotKeyService.MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) m |= HotKeyService.MOD_WIN;
        return m;
    }

    public static (uint modifiers, uint vk) FromWpf(Key key, ModifierKeys mods)
    {
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return (FromWpfModifiers(mods), vk);
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System;
}
