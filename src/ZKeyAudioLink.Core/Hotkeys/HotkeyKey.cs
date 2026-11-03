namespace ZKeyAudioLink.Core.Hotkeys;

/// <summary>
/// 可选的全局播放快捷键。只支持主键盘区单键；修饰键不参与匹配。
/// Name 是写进配置的稳定标识，DisplayName 是界面显示名。
/// </summary>
public sealed record HotkeyKey(
    string Name,
    string DisplayName,
    ushort VirtualKey,
    ushort ScanCode,
    bool UseScanCodeFallback)
{
    public override string ToString() => DisplayName;
}

public static class HotkeyKeys
{
    private static readonly IReadOnlyList<HotkeyKey> KeyList = Build();

    public static IReadOnlyList<HotkeyKey> All => KeyList;

    public static HotkeyKey Default { get; } = KeyList.First(k => k.Name == "Q");

    public static HotkeyKey StopDefault { get; } = KeyList.First(k => k.Name == "F8");

    public static HotkeyKey GetOrDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Default;
        }

        return KeyList.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }

    public static HotkeyKey GetStopOrDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return StopDefault;
        }

        return KeyList.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) ?? StopDefault;
    }

    public static HotkeyKey ResolveStopKey(HotkeyKey play, string? stopName)
    {
        var stop = GetStopOrDefault(stopName);
        if (stop != play)
        {
            return stop;
        }

        // 仅用于损坏/手工改坏的配置做确定性回退；正常 UI 选择会直接拒绝同键。
        return play.Name == StopDefault.Name ? GetOrDefault("Q") : StopDefault;
    }

    public static bool TryGet(string? name, out HotkeyKey key)
    {
        key = GetOrDefault(name);
        return !string.IsNullOrWhiteSpace(name) &&
               string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<HotkeyKey> Build()
    {
        var keys = new List<HotkeyKey>();

        // A-Z
        Add(keys, "Q", "Q", 0x51, 0x10);
        Add(keys, "W", "W", 0x57, 0x11);
        Add(keys, "E", "E", 0x45, 0x12);
        Add(keys, "R", "R", 0x52, 0x13);
        Add(keys, "T", "T", 0x54, 0x14);
        Add(keys, "Y", "Y", 0x59, 0x15);
        Add(keys, "U", "U", 0x55, 0x16);
        Add(keys, "I", "I", 0x49, 0x17);
        Add(keys, "O", "O", 0x4F, 0x18);
        Add(keys, "P", "P", 0x50, 0x19);
        Add(keys, "A", "A", 0x41, 0x1E);
        Add(keys, "S", "S", 0x53, 0x1F);
        Add(keys, "D", "D", 0x44, 0x20);
        Add(keys, "F", "F", 0x46, 0x21);
        Add(keys, "G", "G", 0x47, 0x22);
        Add(keys, "H", "H", 0x48, 0x23);
        Add(keys, "J", "J", 0x4A, 0x24);
        Add(keys, "K", "K", 0x4B, 0x25);
        Add(keys, "L", "L", 0x4C, 0x26);
        Add(keys, "Z", "Z", 0x5A, 0x2C);
        Add(keys, "X", "X", 0x58, 0x2D);
        Add(keys, "C", "C", 0x43, 0x2E);
        Add(keys, "V", "V", 0x56, 0x2F);
        Add(keys, "B", "B", 0x42, 0x30);
        Add(keys, "N", "N", 0x4E, 0x31);
        Add(keys, "M", "M", 0x4D, 0x32);

        // 0-9
        Add(keys, "1", "1", 0x31, 0x02);
        Add(keys, "2", "2", 0x32, 0x03);
        Add(keys, "3", "3", 0x33, 0x04);
        Add(keys, "4", "4", 0x34, 0x05);
        Add(keys, "5", "5", 0x35, 0x06);
        Add(keys, "6", "6", 0x36, 0x07);
        Add(keys, "7", "7", 0x37, 0x08);
        Add(keys, "8", "8", 0x38, 0x09);
        Add(keys, "9", "9", 0x39, 0x0A);
        Add(keys, "0", "0", 0x30, 0x0B);

        // F1-F12
        for (var i = 0; i < 12; i++)
        {
            var scan = i < 10 ? (ushort)(0x3B + i) : (ushort)(i == 10 ? 0x57 : 0x58);
            keys.Add(new HotkeyKey($"F{i + 1}", $"F{i + 1}", (ushort)(0x70 + i), scan, false));
        }

        // 常用控制键
        keys.Add(new HotkeyKey("Space", "空格", 0x20, 0x39, false));
        keys.Add(new HotkeyKey("Tab", "Tab", 0x09, 0x0F, false));
        keys.Add(new HotkeyKey("Enter", "回车", 0x0D, 0x1C, false));
        keys.Add(new HotkeyKey("Escape", "Esc", 0x1B, 0x01, false));
        keys.Add(new HotkeyKey("Backspace", "退格", 0x08, 0x0E, false));
        keys.Add(new HotkeyKey("Insert", "Insert", 0x2D, 0x52, false));
        keys.Add(new HotkeyKey("Delete", "Delete", 0x2E, 0x53, false));
        keys.Add(new HotkeyKey("Home", "Home", 0x24, 0x47, false));
        keys.Add(new HotkeyKey("End", "End", 0x23, 0x4F, false));
        keys.Add(new HotkeyKey("PageUp", "PageUp", 0x21, 0x49, false));
        keys.Add(new HotkeyKey("PageDown", "PageDown", 0x22, 0x51, false));
        keys.Add(new HotkeyKey("Left", "←", 0x25, 0x4B, false));
        keys.Add(new HotkeyKey("Up", "↑", 0x26, 0x48, false));
        keys.Add(new HotkeyKey("Right", "→", 0x27, 0x4D, false));
        keys.Add(new HotkeyKey("Down", "↓", 0x28, 0x50, false));

        // 主键盘区标点；Raw Input 的 VKey 可能随布局变化，保留扫描码兜底
        Add(keys, "OemMinus", "-", 0xBD, 0x0C);
        Add(keys, "OemPlus", "=", 0xBB, 0x0D);
        Add(keys, "OemOpenBrackets", "[", 0xDB, 0x1A);
        Add(keys, "OemCloseBrackets", "]", 0xDD, 0x1B);
        Add(keys, "OemSemicolon", ";", 0xBA, 0x27);
        Add(keys, "OemQuotes", "'", 0xDE, 0x28);
        Add(keys, "OemTilde", "`", 0xC0, 0x29);
        Add(keys, "OemBackslash", "\\", 0xDC, 0x2B);
        Add(keys, "OemComma", ",", 0xBC, 0x33);
        Add(keys, "OemPeriod", ".", 0xBE, 0x34);
        Add(keys, "OemQuestion", "/", 0xBF, 0x35);

        return keys;
    }

    private static void Add(List<HotkeyKey> keys, string name, string displayName, ushort virtualKey, ushort scanCode) =>
        keys.Add(new HotkeyKey(name, displayName, virtualKey, scanCode, true));
}

