namespace ZKeyAudioLink.Core.Hotkeys;

/// <summary>
/// 全局快捷键去重状态，独立于消息窗口，便于对“长按只触发一次 / 多键盘 / 输入法”逻辑做单元测试。
/// Raw Input 本身不会吞键；这里只决定什么时候发出一次 Toggle。
/// </summary>
public sealed class HotkeyState
{
    public const ushort RiKeyE0 = 0x0002;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;

    private readonly object _gate = new();
    private readonly HashSet<long> _downDevices = new();
    private HotkeyKey _key = HotkeyKeys.Default;

    public HotkeyKey Key
    {
        get
        {
            lock (_gate)
            {
                return _key;
            }
        }
    }

    public void SetKey(HotkeyKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_key == key)
            {
                return;
            }

            _key = key;
            _downDevices.Clear();
        }
    }

    /// <summary>
    /// 返回 true 表示这次按下应触发一次播放/停止切换。自动重复、第二把键盘同时按住都不会重复触发。
    /// </summary>
    public bool Handle(long deviceId, ushort virtualKey, ushort makeCode, ushort flags, uint message)
    {
        lock (_gate)
        {
            if (!Matches(_key, virtualKey, makeCode, flags))
            {
                return false;
            }

            if (message == WmKeyDown)
            {
                var first = _downDevices.Count == 0;
                _downDevices.Add(deviceId);
                return first;
            }

            if (message == WmKeyUp)
            {
                _downDevices.Remove(deviceId);
            }

            return false;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _downDevices.Clear();
        }
    }

    public static bool Matches(HotkeyKey key, ushort virtualKey, ushort makeCode, ushort flags)
    {
        if (virtualKey == key.VirtualKey)
        {
            return true;
        }

        // 输入法可能把字母/数字的 VKey 变成 VK_PROCESSKEY；物理扫描码仍然可靠。
        // 扩展键（E0）的扫描码可能与小键盘重叠，因此不参与兜底匹配。
        return key.UseScanCodeFallback &&
               makeCode == key.ScanCode &&
               (flags & RiKeyE0) == 0;
    }
}
