using NAudio.CoreAudioApi;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, DataFlow Flow, DeviceState State)
{
    public bool IsActive => State == DeviceState.Active;

    /// <summary>VB-CABLE 的 16 通道输入是另一个端点，不是 QQ/OOPZ 读取的标准 CABLE Output 对应的那个。</summary>
    public bool IsCableSixteenChannel =>
        Flow == DataFlow.Render &&
        Name.Contains("CABLE In 16ch", StringComparison.OrdinalIgnoreCase);

    public bool IsVirtualCable =>
        Flow == DataFlow.Render &&
        !IsCableSixteenChannel &&
        (Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
         (Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
          Name.Contains("Cable", StringComparison.OrdinalIgnoreCase)));

    public bool IsCableOutput =>
        Flow == DataFlow.Capture &&
        (Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
         (Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
          Name.Contains("Cable", StringComparison.OrdinalIgnoreCase)));

    public string StateText => State switch
    {
        DeviceState.Active => "可用",
        DeviceState.Disabled => "已禁用",
        DeviceState.NotPresent => "未连接",
        DeviceState.Unplugged => "已拔出",
        _ => State.ToString()
    };
}

/// <summary>
/// Enumerates endpoints by endpoint ID (never by display name) and listens for
/// device arrival/removal. Selecting a device is always explicit: the service
/// never substitutes another microphone or speaker.
/// </summary>
public sealed class DeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly AppLog? _log;
    private MMDeviceNotificationClient? _notifier;
    private bool _disposed;

    public DeviceService(AppLog? log = null)
    {
        _log = log;
        try
        {
            _notifier = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifier.DeviceAdded += (_, _) => RaiseChanged("added");
            _notifier.DeviceRemoved += (_, _) => RaiseChanged("removed");
            _notifier.DeviceStateChanged += (_, _) => RaiseChanged("state");
            _notifier.DefaultDeviceChanged += (_, _) => RaiseChanged("default");
        }
        catch (Exception ex)
        {
            _log?.Warning("device notifications unavailable", new { error = ex.Message });
            _notifier = null;
        }
    }

    public event Action? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => GetDevices(DataFlow.Render);
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => GetDevices(DataFlow.Capture);

    public AudioDeviceInfo? Find(string? id, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return GetDevices(flow).FirstOrDefault(d => d.Id == id);
    }

    public AudioDeviceInfo? GetDefault(DataFlow flow, Role role)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, role))
            {
                return null;
            }

            using var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return ToInfo(device);
        }
        catch (Exception ex)
        {
            _log?.Warning("default device lookup failed", new { flow = flow.ToString(), error = ex.Message });
            return null;
        }
    }

    public AudioDeviceInfo? FindVirtualCable() =>
        GetRenderDevices().FirstOrDefault(d => d.IsVirtualCable && d.IsActive);

    public AudioDeviceInfo? FindCableOutput() =>
        GetCaptureDevices().FirstOrDefault(d => d.IsCableOutput && d.IsActive);

    /// <summary>Returns a newly created MMDevice for the given endpoint id, or null.</summary>
    public MMDevice? TryGetDevice(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            var info = GetDevices(DataFlow.Render).FirstOrDefault(d => d.Id == id) ??
                       GetDevices(DataFlow.Capture).FirstOrDefault(d => d.Id == id);
            if (info is null || !info.IsActive)
            {
                return null;
            }

            return _enumerator.GetDevice(id);
        }
        catch (Exception ex)
        {
            _log?.Warning("device open failed", new { id, error = ex.Message });
            return null;
        }
    }

    private IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        try
        {
            using var collection = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
            var result = new List<AudioDeviceInfo>(collection.Count);
            for (var i = 0; i < collection.Count; i++)
            {
                using var device = collection[i];
                result.Add(ToInfo(device));
            }

            return result;
        }
        catch (Exception ex)
        {
            _log?.Warning("device enumeration failed", new { flow = flow.ToString(), error = ex.Message });
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    private static AudioDeviceInfo ToInfo(MMDevice device) =>
        new(device.ID, device.FriendlyName, device.DataFlow, device.State);

    private void RaiseChanged(string reason)
    {
        _log?.Info("device change", new { reason });
        try
        {
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Error("device change handler failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _notifier?.Dispose();
        }
        catch
        {
            // ignore
        }

        _notifier = null;
        try
        {
            _enumerator.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
