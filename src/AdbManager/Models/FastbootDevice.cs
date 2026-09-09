namespace AdbManager.Models;

/// <summary>Fastboot（Bootloader）模式下的设备。</summary>
public sealed class FastbootDevice
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Serial) ? State : $"{Serial} ({State})";
}
