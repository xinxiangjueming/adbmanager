using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AdbManager.Services;

/// <summary>
/// 圆表截图加工：手表的帧缓冲是方形（圆形表盘以外四角是黑的），直接截图分享观感像方形屏。
/// 把内切圆以外的像素向浅白 #F5F5F5 过渡（边缘约 1.5px 抗锯齿），与投屏的圆形遮罩同款。
/// 用系统自带 WIC（Windows.Graphics.Imaging）解码 / 编码 PNG，无第三方图像库依赖。
/// </summary>
internal static class WatchImageMask
{
    // 圆外填充色：浅白 #F5F5F5（与 ScrcpyCircleMask 一致）
    private const byte FillColor = 0xF5;
    private const double Aa = 1.5;

    /// <summary>把 PNG 截图中内切圆以外的区域混向浅白色。输入输出均为 PNG 字节。</summary>
    public static async Task<byte[]> ApplyToPngAsync(byte[] png)
    {
        // 解码：写入内存流后交给 WIC
        var src = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(src))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        var decoder = await BitmapDecoder.CreateAsync(src);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var pixels = pixelData.DetachPixelData();
        var w = (int)decoder.OrientedPixelWidth;
        var h = (int)decoder.OrientedPixelHeight;

        BlendCircleMask(pixels, w, h);

        // 编码回 PNG（保留原 DPI）
        var dst = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, dst);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)w, (uint)h, decoder.DpiX, decoder.DpiY, pixels);
        await encoder.FlushAsync();

        using var reader = new DataReader(dst.GetInputStreamAt(0));
        await reader.LoadAsync((uint)dst.Size); // dst.Size 是 ulong，LoadAsync/数组长度都收 uint/int
        var result = new byte[dst.Size];
        reader.ReadBytes(result);
        return result;
    }

    /// <summary>逐像素把内切圆以外区域混向浅白（BGRA，不动 alpha）。与 ScrcpyCircleMask 的几何完全一致。</summary>
    private static void BlendCircleMask(byte[] pixels, int w, int h)
    {
        var cx = w / 2.0;
        var cy = h / 2.0;
        var r = Math.Min(w, h) / 2.0 - 0.5; // 略收半像素，避免啃到画面最外圈

        for (var py = 0; py < h; py++)
        {
            var dy = py + 0.5 - cy;
            var row = py * w * 4;
            for (var px = 0; px < w; px++)
            {
                var dx = px + 0.5 - cx;
                var t = (Math.Sqrt(dx * dx + dy * dy) - r) / Aa + 0.5; // r 处半透明，向内外各 aa/2 过渡
                if (t <= 0) continue;                                  // 圆内：保持原样
                if (t > 1) t = 1;

                var i = row + px * 4;
                pixels[i] = Blend(pixels[i], t);
                pixels[i + 1] = Blend(pixels[i + 1], t);
                pixels[i + 2] = Blend(pixels[i + 2], t);
            }
        }
    }

    private static byte Blend(byte c, double t) => (byte)Math.Round(c + (FillColor - c) * t);
}
