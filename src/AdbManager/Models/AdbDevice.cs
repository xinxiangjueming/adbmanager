using AdbManager.Services;
using System.Collections.Generic;

namespace AdbManager.Models;

/// <summary>一台已连接（或已发现）的 Android 设备。</summary>
public sealed class AdbDevice
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";          // device / unauthorized / offline / recovery / sideload
    public string Model { get; set; } = "";          // ro.product.model
    public string Product { get; set; } = "";        // ro.product.name
    public string DeviceCodename { get; set; } = ""; // ro.product.device
    public string TransportId { get; set; } = "";
    public string UsbPort { get; set; } = "";        // usb:2-1，空则为 TCP

    /// <summary>USB 有线 / Wi-Fi 无线。</summary>
    /// <remarks>
    /// 判定依据是 serial 形态而非 UsbPort：部分 HyperOS 机型（platform-tools 37 实测，
    /// 24031PN0DC / Android 16）在 USB 有线连接下 adb devices -l 不输出 usb: 字段，
    /// 仅凭 UsbPort 为空会误判为无线。网络 transport 的 serial 必为 ip:port（含冒号）
    /// 或 mDNS 服务实例名（IsMdns）；裸 serial 只可能来自本地 USB 枚举。
    /// </remarks>
    public string ConnectionKind => Serial.Contains(':') || IsMdns
        ? Get("Model_ConnectionWireless")
        : Get("Model_ConnectionUsb");

    /// <summary>
    /// 是否为 mDNS（无线调试）设备：serial 是服务实例名（如 adb-xxxx._adb-tls-connect._tcp）
    /// 而非 ip:port。这类设备 adb disconnect 无法按 serial 断开（只接受 HOST[:PORT]）。
    /// </summary>
    public bool IsMdns => Serial.Contains("._adb-tls-", StringComparison.OrdinalIgnoreCase)
                          || Serial.EndsWith("._tcp", StringComparison.OrdinalIgnoreCase);

    public bool IsOnline => State == "device";

    public string DisplayName => string.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' ');

    public string Subtitle => string.IsNullOrEmpty(Product) ? DeviceCodename : Product;

    /// <summary>状态徽标文本。</summary>
    private static string Get(string key) => LocalizationService.Get(key);

    public string StateText => State switch
    {
        "device" => Get("Model_StateConnected"),
        "unauthorized" => Get("Model_StateUnauthorized"),
        "offline" => Get("Model_StateOffline"),
        "recovery" => Get("Model_StateRecovery"),
        "sideload" => Get("Model_StateSideload"),
        "bootloader" or "fastboot" => Get("Model_StateFastboot"),
        _ => State
    };

    public Dictionary<string, string> Props { get; set; } = new();
}
