namespace AdbManager.Models;

/// <summary>设备运存占用快照（来自 /proc/meminfo）。</summary>
public sealed record DeviceMemoryInfo(long TotalKb, long AvailableKb)
{
    /// <summary>已用 ≈ 总量 - 可用（与系统设置里的口径一致，可用含可回收缓存）。</summary>
    public long UsedKb => Math.Max(0, TotalKb - AvailableKb);

    public double UsedPercent => TotalKb > 0 ? UsedKb * 100.0 / TotalKb : 0;

    public double TotalGb => TotalKb / 1024.0 / 1024.0;

    public double UsedGb => UsedKb / 1024.0 / 1024.0;
}
