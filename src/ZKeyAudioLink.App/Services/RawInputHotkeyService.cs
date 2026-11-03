using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using ZKeyAudioLink.Core.Hotkeys;

namespace ZKeyAudioLink.App.Services;

/// <summary>
/// Background global hotkey using Raw Input with RIDEV_INPUTSINK. Raw Input is
/// read-only: the key still reaches the focused window, unlike a swallowing
/// low-level hook. Auto-repeat is suppressed by <see cref="HotkeyState"/>;
/// Shift/Ctrl/Alt combinations still count as the selected key.
/// </summary>
public sealed class RawInputHotkeyService : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;

    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEKEYBOARD = 1;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource? _source;
    private readonly HotkeyState _playState = new();
    private readonly HotkeyState _stopState = new();
    private bool _disposed;
    private bool _enabled;

    public RawInputHotkeyService()
    {
        try
        {
            var parameters = new HwndSourceParameters("ZKeyAudioLinkRawInput")
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                Width = 0,
                Height = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            LastError = $"无法创建后台按键窗口：{ex.Message}";
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public event Action? PlayRequested;
    public event Action? StopRequested;
    public event Action<string>? Failed;

    public bool IsEnabled => _enabled;
    public HotkeyKey PlayKey => _playState.Key;
    public HotkeyKey StopKey => _stopState.Key;
    public string? LastError { get; private set; }

    public void SetKeys(HotkeyKey play, HotkeyKey stop)
    {
        _playState.SetKey(play);
        _stopState.SetKey(stop);
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _source is null)
        {
            return;
        }

        if (enabled == _enabled)
        {
            return;
        }

        if (enabled)
        {
            if (TryRegister())
            {
                _enabled = true;
                _playState.Reset();
                _stopState.Reset();
                LastError = null;
            }
            else
            {
                _enabled = false;
                Failed?.Invoke(LastError ?? "注册全局快捷键失败。");
            }
        }
        else
        {
            Unregister();
            _enabled = false;
            _playState.Reset();
            _stopState.Reset();
        }
    }

    private bool TryRegister()
    {
        var device = new RAWINPUTDEVICE
        {
            UsagePage = HID_USAGE_PAGE_GENERIC,
            Usage = HID_USAGE_GENERIC_KEYBOARD,
            Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
            Target = _source!.Handle
        };

        if (RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        LastError = $"注册后台键盘失败（Win32 {error}）。快捷键可能不可用。";
        return false;
    }

    private static void Unregister()
    {
        try
        {
            var device = new RAWINPUTDEVICE
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_KEYBOARD,
                Flags = RIDEV_REMOVE,
                Target = IntPtr.Zero
            };
            RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch
        {
            // ignore; the window is going away anyway
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT_DEVICE_CHANGE)
        {
            _playState.Reset();
            _stopState.Reset();
            return IntPtr.Zero;
        }

        if (msg != WM_INPUT)
        {
            return IntPtr.Zero;
        }

        try
        {
            ProcessRawInput(lParam);
        }
        catch
        {
            // Never let a malformed raw-input packet take down the UI thread.
        }

        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        var first = GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
        if (first == unchecked((uint)-1) || size == 0 || size > 1024)
        {
            return;
        }

        var buffer = new byte[size];
        GCHandle handle = default;
        try
        {
            handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var read = GetRawInputData(rawInputHandle, RID_INPUT, handle.AddrOfPinnedObject(), ref size, (uint)headerSize);
            if (read == unchecked((uint)-1) || read < headerSize + Marshal.SizeOf<RAWKEYBOARD>())
            {
                return;
            }
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        var header = MemoryMarshal.Read<RAWINPUTHEADER>(buffer);
        if (header.Type != RIM_TYPEKEYBOARD)
        {
            return;
        }

        var keyboard = MemoryMarshal.Read<RAWKEYBOARD>(buffer.AsSpan(headerSize));
        var deviceId = header.Device.ToInt64();
        var play = _playState.Handle(deviceId, keyboard.VKey, keyboard.MakeCode, keyboard.Flags, keyboard.Message);
        var stop = _stopState.Handle(deviceId, keyboard.VKey, keyboard.MakeCode, keyboard.Flags, keyboard.Message);
        if (play)
        {
            PlayRequested?.Invoke();
        }
        else if (stop)
        {
            // 防御：如果配置异常导致两键相同，播放优先，绝不一次按下触发两个动作。
            StopRequested?.Invoke();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
        {
            _playState.Reset();
            _stopState.Reset();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _playState.Reset();
        _stopState.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_enabled)
        {
            Unregister();
            _enabled = false;
        }

        if (_source is not null)
        {
            try
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);
}
