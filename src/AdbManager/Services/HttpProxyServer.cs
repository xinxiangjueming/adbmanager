using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdbManager.Services;

/// <summary>
/// 极简 HTTP 正向代理（支持 CONNECT 隧道与绝对 URI 请求），
/// 配合 adb reverse + 设备系统代理即可把电脑网络共享给设备，无需 root。
/// </summary>
public static class HttpProxyServer
{
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static readonly object Gate = new();

    public static int Port { get; private set; }
    public static bool IsRunning { get; private set; }

    public static void Start(int port)
    {
        lock (Gate)
        {
            Stop();
            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            IsRunning = true;
            _ = AcceptLoopAsync(_cts.Token);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
        }
    }

    private static async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                lock (Gate)
                {
                    if (!IsRunning) break;
                }
                continue;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();

            var head = await ReadHeadAsync(stream, token).ConfigureAwait(false);
            if (head.Length == 0) return;

            var text = Encoding.ASCII.GetString(head);
            var firstLine = text.Split("\r\n")[0];

            if (firstLine.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                // CONNECT host:port HTTP/1.1 —— 建立隧道（HTTPS）
                var target = firstLine.Split(' ')[1];
                var separator = target.LastIndexOf(':');
                var host = target[..separator];
                var port = int.Parse(target[(separator + 1)..]);

                using var remote = new TcpClient();
                await remote.ConnectAsync(host, port, token).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"),
                    token).ConfigureAwait(false);

                using var remoteStream = remote.GetStream();
                await RelayAsync(stream, remoteStream, token).ConfigureAwait(false);
                return;
            }

            // 普通 HTTP：改写为对源站点的直接请求
            var parts = firstLine.Split(' ');
            if (parts.Length < 2) return;
            var uri = new Uri(parts[1]);
            var requestBuilder = new StringBuilder();
            requestBuilder.Append($"{parts[0]} {uri.PathAndQuery} HTTP/1.1\r\n");
            requestBuilder.Append($"Host: {uri.Authority}\r\n");

            foreach (var headerLine in text.Split("\r\n").Skip(1))
            {
                if (headerLine.Length == 0) break;
                if (headerLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
                if (headerLine.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
                if (headerLine.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
                requestBuilder.Append(headerLine + "\r\n");
            }
            requestBuilder.Append("Connection: close\r\n\r\n");

            using var remote2 = new TcpClient();
            await remote2.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 80, token).ConfigureAwait(false);
            await using var remoteStream2 = remote2.GetStream();

            var requestBytes = Encoding.ASCII.GetBytes(requestBuilder.ToString());
            await remoteStream2.WriteAsync(requestBytes, token).ConfigureAwait(false);

            // 客户端请求剩余部分（body）与源站响应双向搬运
            await RelayAsync(stream, remoteStream2, token).ConfigureAwait(false);
        }
        catch
        {
            // 单个连接失败不影响代理整体
        }
    }

    private static async Task<byte[]> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var head = new MemoryStream();
        while (head.Length < 65536)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read <= 0) break;
            head.Write(buffer, 0, read);
            var bytes = head.ToArray();
            if (Encoding.ASCII.GetString(bytes).Contains("\r\n\r\n")) return bytes;
        }
        return head.ToArray();
    }

    /// <summary>双向搬运两个流，任一端关闭/出错即结束并释放。</summary>
    private static async Task RelayAsync(Stream a, Stream b, CancellationToken token)
    {
        var taskA = a.CopyToAsync(b, 65536, token);
        var taskB = b.CopyToAsync(a, 65536, token);
        try
        {
            await Task.WhenAny(taskA, taskB).ConfigureAwait(false);
        }
        catch
        {
            // 任一方向断开即结束
        }
        finally
        {
            try { a.Close(); } catch { }
            try { b.Close(); } catch { }
        }
    }
}
