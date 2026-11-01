namespace ZKeyAudioLink.Core.Hotkeys;

/// <summary>
/// Z 键去重状态，独立于消息窗口，便于对“长按只触发一次 / 多键盘 / 输入法”逻辑做单元测试。
/// Raw Input 本身不会吞键；这里只决定什么时候发出一次 Toggle。
/// </summary>
public sealed class ZKeyState
{
    public const ushort VkZ = 0x5A;
    public const ushort ZScanCode = 0x2C;
    public const ushort RiKeyE0 = 0x0002;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;

    private readonly object _gate = new();
    private readonly HashSet<long> _downDevices = new();

    /// <summary>
    /// 返回 true 表示这次按下应触发一次播放/停止切换。自动重复、第二把键盘同时按住都不会重复触发。
    /// </summary>
    public bool Handle(long deviceId, ushort virtualKey, ushort makeCode, ushort flags, uint message)
    {
        if (!IsZ(virtualKey, makeCode, flags))
        {
            return false;
        }

        lock (_gate)
        {
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
        }

        return false;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _downDevices.Clear();
        }
    }

    public static bool IsZ(ushort virtualKey, ushort makeCode, ushort flags)
    {
        if (virtualKey == VkZ)
        {
            return (flags & RiKeyE0) == 0;
        }

        // 输入法可能把 VKey 变成 VK_PROCESSKEY；物理扫描码仍然可靠。
        // E0 前缀的 0x2C 属于小键盘/扩展键，不是字母 Z。
        return makeCode == ZScanCode && (flags & RiKeyE0) == 0;
    }
}
