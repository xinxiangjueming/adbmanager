using System.Globalization;
using AdbManager.Models;

namespace AdbManager.Services;

public sealed partial class AdbService
{
    /// <summary>列出设备目录内容（ls -la 解析）。</summary>
    public async Task<List<RemoteFileItem>> ListDirectoryAsync(string serial, string path)
    {
        var result = await RunAsync($"-s \"{serial}\" shell ls -la \"{path}\"", timeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        var items = new List<RemoteFileItem>();
        foreach (var rawLine in result.StdOut.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("total", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;

            var permissions = parts[0];
            var isDirectory = permissions.StartsWith('d');
            var isLink = permissions.StartsWith('l');

            var nameIndex = parts.Length >= 8 ? 7 : parts.Length - 1;
            var name = string.Join(' ', parts.Skip(nameIndex));

            // 符号链接：取出 "->" 之前的部分
            var arrow = name.IndexOf("->", StringComparison.Ordinal);
            if (arrow > 0) name = name[..arrow].Trim();

            if (name is "." or "..") continue;

            long.TryParse(parts.Length > 4 ? parts[4] : "0", NumberStyles.Any, CultureInfo.InvariantCulture, out var size);
            var modified = parts.Length >= 7 ? $"{parts[5]} {parts[6]}" : "";

            items.Add(new RemoteFileItem
            {
                Name = name,
                FullPath = (path.TrimEnd('/') + "/" + name).Replace("//", "/"),
                IsDirectory = isDirectory,
                IsLink = isLink,
                Size = size,
                Modified = modified,
                Permissions = permissions
            });
        }

        var directories = items.Where(i => i.IsDirectory).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var files = items.Where(i => !i.IsDirectory).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        return directories.Concat(files).ToList();
    }

    public async Task<(bool Success, string Message)> PushAsync(string serial, string localPath, string remoteDir)
    {
        var result = await RunAsync($"-s \"{serial}\" push \"{localPath}\" \"{remoteDir}\"", null, TimeSpan.FromMinutes(30))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> PullAsync(string serial, string remotePath, string localFile)
    {
        var result = await RunAsync($"-s \"{serial}\" pull \"{remotePath}\" \"{localFile}\"", null, TimeSpan.FromMinutes(30))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> DeleteRemoteAsync(string serial, string remotePath)
    {
        var result = await RunAsync($"-s \"{serial}\" shell rm -rf \"{remotePath}\"", null, TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> MakeDirectoryAsync(string serial, string remotePath)
    {
        var result = await RunAsync($"-s \"{serial}\" shell mkdir -p \"{remotePath}\"", null, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> MoveRemoteAsync(string serial, string from, string to)
    {
        var result = await RunAsync($"-s \"{serial}\" shell mv \"{from}\" \"{to}\"", null, TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<(bool Success, string Message)> CopyRemoteAsync(string serial, string from, string to)
    {
        var result = await RunAsync($"-s \"{serial}\" shell cp -r \"{from}\" \"{to}\"", null, TimeSpan.FromMinutes(10))
            .ConfigureAwait(false);
        return (result.Success, result.Message);
    }
}
