using System.Collections.ObjectModel;
using AdbManager.Models;

namespace AdbManager.Services;

/// <summary>进程内共享状态：服务实例、设备列表、当前选中设备、日志。</summary>
public static class AppState
{
    public static AdbService Adb { get; } = new();
    public static ScrcpyService Scrcpy { get; } = new();
    public static Logger Log { get; } = new();
    public static ObservableCollection<AdbDevice> Devices { get; } = new();

    private static AdbDevice? _currentDevice;
    private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    /// <summary>在 UI 线程启动时调用一次，缓存调度器用于跨线程回 UI。</summary>
    public static void AttachDispatcher(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public static AdbDevice? CurrentDevice
    {
        get => _currentDevice;
        set
        {
            _currentDevice = value;
            CurrentDeviceChanged?.Invoke(value);
        }
    }

    public static event Action<AdbDevice?>? CurrentDeviceChanged;
    public static event Action? DevicesChanged;

    public static bool HasDevice => Devices.Count > 0;

    /// <summary>刷新设备列表，尽量保留原有对象引用以维持选中状态。</summary>
    public static async Task RefreshDevicesAsync()
    {
        try
        {
            var fresh = await Adb.GetDevicesAsync().ConfigureAwait(false);
            var dispatcher = _dispatcher;

            void Apply()
            {
                // 本轮是否存在任何设备层面的变化（增删或字段更新）。
                var changed = false;
                // CurrentDevice 赋值本身就会触发事件，避免重复补发。
                var currentDeviceNotified = false;

                // 先移除已消失的设备
                for (var i = Devices.Count - 1; i >= 0; i--)
                {
                    var serial = Devices[i].Serial;
                    if (fresh.All(d => d.Serial != serial))
                    {
                        if (ReferenceEquals(CurrentDevice, Devices[i]))
                        {
                            CurrentDevice = null;
                            currentDeviceNotified = true;
                        }
                        Devices.RemoveAt(i);
                        changed = true;
                    }
                }

                foreach (var incoming in fresh)
                {
                    var existing = Devices.FirstOrDefault(d => d.Serial == incoming.Serial);
                    if (existing is null)
                    {
                        Devices.Add(incoming);
                        changed = true;
                        continue;
                    }

                    // 就地更新已有对象（保持引用不变，选中状态不丢）。
                    // 注意：引用不变 => CurrentDevice setter 不会触发，故必须在此显式记录变化，
                    // 否则「设备已连接但状态从 offline/unauthorized 变为 device」这类转换
                    // 永远不会有事件，页面只能靠用户手动点刷新（历史 bug）。
                    if (existing.State != incoming.State ||
                        existing.Model != incoming.Model ||
                        existing.Product != incoming.Product ||
                        existing.DeviceCodename != incoming.DeviceCodename ||
                        existing.TransportId != incoming.TransportId ||
                        existing.UsbPort != incoming.UsbPort)
                    {
                        existing.State = incoming.State;
                        existing.Model = incoming.Model;
                        existing.Product = incoming.Product;
                        existing.DeviceCodename = incoming.DeviceCodename;
                        existing.TransportId = incoming.TransportId;
                        existing.UsbPort = incoming.UsbPort;
                        changed = true;
                    }
                }

                if (CurrentDevice is null && Devices.Count > 0)
                {
                    CurrentDevice = Devices.FirstOrDefault(d => d.IsOnline) ?? Devices[0];
                    // 赋 CurrentDevice 已触发 CurrentDeviceChanged，无需再补
                    currentDeviceNotified = true;
                }

                DevicesChanged?.Invoke();

                // 设备本身没换、但状态等字段变了（如 offline → device）：
                // 主动补发一次 CurrentDeviceChanged，让各页面按「同一设备的重新可用」重新加载。
                if (changed && !currentDeviceNotified) CurrentDeviceChanged?.Invoke(CurrentDevice);
            }

            if (dispatcher is not null && !dispatcher.HasThreadAccess) dispatcher.TryEnqueue(Apply);
            else Apply();
        }
        catch (Exception ex)
        {
            Log.Error(LocalizationService.Get("Adb_Err_Refresh", ex.Message));
        }
    }
}
