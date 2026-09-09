using AdbManager.Services;

namespace AdbManager.Models;

public sealed class PackageInfo
{
    public string PackageName { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool IsDisabled { get; set; }

    private static string Get(string key) => LocalizationService.Get(key);

    public string KindText => IsSystem ? Get("Model_PkgSystem") : Get("Model_PkgThirdParty");
    public string StateText => IsDisabled ? Get("Model_PkgDisabled") : Get("Model_PkgNormal");

    public override string ToString() => $"{PackageName}   [{KindText} / {StateText}]";
}
