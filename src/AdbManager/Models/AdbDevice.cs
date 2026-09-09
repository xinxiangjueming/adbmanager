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
    public string ConnectionKind => string.IsNullOrEmpty(UsbPort) ? Get("Model_ConnectionWireless") : Get("Model_ConnectionUsb");

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
