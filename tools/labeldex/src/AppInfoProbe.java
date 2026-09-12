// AppInfoProbe - 一次性取回应用名 + 图标（原创实现）
// 用法: CLASSPATH=<dex> app_process /system/bin AppInfoProbe <outDir|-> [-s size] <pkg...>
//   outDir 为 "-" 时只取应用名，不生成图标（省掉 PNG 编码与磁盘写入）
// 输出: 每行 "包名\t应用名\t图标PNG路径"；名称失败为 <ERR>；图标失败路径为空
//
// 关键点：
//   1. ActivityThread 是隐藏 API，必须纯反射
//   2. systemMain().getSystemContext() 已能按当前用户解析全部包（含第三方）
//   3. 图标经 createPackageContext + getDrawableForDensity 逐档密度尝试
//   4. Drawable -> Bitmap(ARGB_8888) -> PNG，按目标尺寸等比缩放以控制体积
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Looper;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;

public final class AppInfoProbe {
    private AppInfoProbe() {}

    /** 图标目标尺寸（px）。缩放可显著减小体积，避免 3600px 巨图。 */
    private static int targetSize = 96;

    private static String safe(String s) {
        if (s == null) return "";
        return s.replace('\t', ' ').replace('\n', ' ').replace('\r', ' ');
    }

    /** Drawable 等比缩放到 targetSize 内，输出 PNG 路径；失败返回 null。 */
    private static String writeIcon(Drawable drawable, String outputPath) {
        if (drawable == null) return null;
        int w = drawable.getIntrinsicWidth();
        int h = drawable.getIntrinsicHeight();
        if (w <= 0 || h <= 0) return null;

        // 等比缩放到目标尺寸（只缩不放，避免小图被放大糊掉）
        float scale = 1f;
        int maxSide = Math.max(w, h);
        if (maxSide > targetSize) scale = (float) targetSize / maxSide;
        int bw = Math.max(1, Math.round(w * scale));
        int bh = Math.max(1, Math.round(h * scale));

        Bitmap bitmap = Bitmap.createBitmap(bw, bh, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bitmap);
        canvas.scale(scale, scale);
        bitmap.eraseColor(0);
        drawable.setBounds(0, 0, w, h);
        drawable.draw(canvas);
        try (FileOutputStream fos = new FileOutputStream(new File(outputPath));
             BufferedOutputStream bos = new BufferedOutputStream(fos, 8192)) {
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, bos);
            bos.flush();
            return outputPath;
        } catch (Throwable t) {
            return null;
        } finally {
            bitmap.recycle();
        }
    }

    private static Drawable loadIcon(String pkg, Context context) {
        PackageManager pm = context.getPackageManager();
        try {
            ApplicationInfo info = pm.getApplicationInfo(pkg, 0);
            Context pkgContext = context.createPackageContext(pkg, Context.CONTEXT_IGNORE_SECURITY);
            int[] densities = {320, 240, 213, 160, 480, 640};
            for (int d : densities) {
                try {
                    Drawable dw = pkgContext.getResources().getDrawableForDensity(info.icon, d);
                    if (dw != null) return dw;
                } catch (Throwable ignored) { }
            }
            return info.loadIcon(pm);
        } catch (Throwable t) {
            return null;
        }
    }

    public static void main(String[] args) {
        if (Looper.getMainLooper() == null) Looper.prepareMainLooper();
        if (args.length < 2) {
            System.out.println("USAGE\t<outDir> [-s size] <pkg...>");
            return;
        }

        int argStart = 1;
        String outDir = args[0];
        // outDir 为 "-" 表示只要应用名（label-only 模式），跳过图标生成
        boolean wantIcons = !"-".equals(outDir);
        // 可选 -s <size> 指定图标目标边长
        if (args.length >= 3 && "-s".equals(args[1])) {
            try {
                targetSize = Integer.parseInt(args[2]);
            } catch (Throwable ignored) { }
            argStart = 3;
        }
        if (wantIcons) new File(outDir).mkdirs();

        Context context;
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            Object thread = at.getMethod("systemMain").invoke(null);
            context = (Context) at.getMethod("getSystemContext").invoke(thread);
        } catch (Throwable t) {
            System.out.println("FATAL\t" + t);
            return;
        }
        PackageManager pm = context.getPackageManager();

        for (int i = argStart; i < args.length; i++) {
            String pkg = args[i];
            if (pkg == null || pkg.isEmpty()) continue;

            String label;
            try {
                ApplicationInfo info = pm.getApplicationInfo(pkg, 0);
                CharSequence cs = pm.getApplicationLabel(info);
                label = safe(cs == null ? "" : cs.toString());
                if (label.isEmpty()) label = "<ERR>";
            } catch (Throwable t) {
                label = "<ERR>";
            }

            String iconPath = "";
            if (wantIcons) {
                try {
                    iconPath = writeIcon(loadIcon(pkg, context), outDir + "/" + pkg + ".png");
                    if (iconPath == null) iconPath = "";
                } catch (Throwable ignored) { }
            }

            System.out.println(pkg + "\t" + label + "\t" + iconPath);
        }
    }
}
