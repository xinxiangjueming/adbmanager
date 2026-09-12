// AppIconProbe - 验证 app_process 下能否取到应用图标并导出 PNG（原创实现）
// 依据公开资料：PackageManager + createPackageContext + getDrawableForDensity 取图标资源。
// 注意：ActivityThread 是隐藏 API，公开 android.jar 无此类，必须走纯反射。
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.pm.LauncherApps;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Looper;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Method;

public final class AppIconProbe {
    private AppIconProbe() {}

    private static int failures;

    private static void writePng(String tag, Drawable drawable, String outputPath) {
        if (drawable == null) {
            System.out.println("NULL\t" + outputPath);
            failures++;
            return;
        }
        int width = drawable.getIntrinsicWidth();
        int height = drawable.getIntrinsicHeight();
        if (width <= 0 || height <= 0) {
            System.out.println("ZERO\t" + width + "x" + height);
            failures++;
            return;
        }
        Bitmap bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bitmap);
        bitmap.eraseColor(0);
        drawable.setBounds(0, 0, width, height);
        drawable.draw(canvas);
        try (FileOutputStream fos = new FileOutputStream(new File(outputPath));
             BufferedOutputStream bos = new BufferedOutputStream(fos, 8192)) {
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, bos);
            bos.flush();
            System.out.println("OK\t" + tag + "\t" + outputPath + "\t" + width + "x" + height);
        } catch (Exception e) {
            System.out.println("ERR\t" + e);
            failures++;
        } finally {
            bitmap.recycle();
        }
    }

    /** 路径 A：LauncherApps（Android 官方推荐，天然按当前用户） */
    private static Drawable loadIconViaLauncher(Context context, String pkg) {
        try {
            Object svc = context.getSystemService(Context.LAUNCHER_APPS_SERVICE);
            if (!(svc instanceof LauncherApps)) return null;
            LauncherApps la = (LauncherApps) svc;
            for (android.os.UserHandle handle : la.getProfiles()) {
                try {
                    java.util.List<?> acts = la.getActivityList(pkg, handle);
                    if (acts == null || acts.isEmpty()) continue;
                    for (Object act : acts) {
                        Object info = act.getClass().getMethod("getActivityInfo").invoke(act);
                        if (info == null) continue;
                        java.lang.reflect.Field iconF = null;
                        try { iconF = info.getClass().getField("icon"); } catch (Throwable ignored) { }
                        if (iconF == null) continue;
                        Object iconRes = iconF.get(info);
                        if (!(iconRes instanceof Integer)) continue;
                        int resId = (Integer) iconRes;
                        if (resId == 0) continue;
                        Object pm = act.getClass().getMethod("getPackageName").invoke(act);
                        // 用 ActivityInfo.loadIcon(pm) 取最终图标（含自适应图标处理）
                        Object pmReal = context.getPackageManager();
                        Drawable dw = (Drawable) info.getClass()
                                .getMethod("loadIcon", Class.forName("android.content.pm.PackageManager"))
                                .invoke(info, pmReal);
                        if (dw != null) return dw;
                    }
                } catch (Throwable ignored) { }
            }
        } catch (Throwable ignored) { }
        return null;
    }

    /** 路径 B：createContextAsUser + createPackageContext（原方案，按指定用户） */
    private static Drawable loadIconViaUserContext(Context base, int userId, String pkg) {
        try {
            Class<?> userHandleCls = Class.forName("android.os.UserHandle");
            Object userHandle = userHandleCls.getMethod("of", int.class).invoke(null, userId);
            Method m = Class.forName("android.content.Context")
                    .getMethod("createContextAsUser",
                            Class.forName("android.os.UserHandle"), int.class);
            Context userCtx = (Context) m.invoke(base, userHandle, 0);
            PackageManager pm = userCtx.getPackageManager();
            ApplicationInfo info = pm.getApplicationInfo(pkg, 0);
            Context pkgContext = userCtx.createPackageContext(pkg, Context.CONTEXT_IGNORE_SECURITY);
            int[] densities = {320, 240, 213, 160, 480, 640};
            for (int d : densities) {
                try {
                    Drawable dw = pkgContext.getResources().getDrawableForDensity(info.icon, d);
                    if (dw != null) return dw;
                } catch (Throwable ignored) { }
            }
            return info.loadIcon(pm);
        } catch (Throwable t) {
            System.out.println("USERCTXFAIL\t" + pkg + "\t" + t.getClass().getSimpleName());
            return null;
        }
    }

    public static void main(String[] args) {
        if (Looper.getMainLooper() == null) Looper.prepareMainLooper();
        if (args.length < 2) {
            System.out.println("USAGE\t<outDir> <pkg...>");
            return;
        }
        String outDir = args[0];
        new File(outDir).mkdirs();

        Context context;
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            Object thread = at.getMethod("systemMain").invoke(null);
            context = (Context) at.getMethod("getSystemContext").invoke(thread);
        } catch (Throwable t) {
            System.out.println("FATAL\t" + t);
            return;
        }

        for (int i = 1; i < args.length; i++) {
            String pkg = args[i];
            if (pkg == null || pkg.isEmpty()) continue;
            String outPath = outDir + "/" + pkg + ".png";

            long t0 = System.currentTimeMillis();
            Drawable icon = loadIconViaLauncher(context, pkg);
            long tLauncher = System.currentTimeMillis() - t0;

            String tag = "L";
            if (icon == null) {
                t0 = System.currentTimeMillis();
                icon = loadIconViaUserContext(context, 0, pkg);
                tag = "U" + (System.currentTimeMillis() - t0) + "ms";
            }
            System.out.println("SRC\t" + pkg + "\t" + tag + "\tlauncher=" + tLauncher + "ms");
            writePng(tag, icon, outPath);
        }
        System.out.println("FAILURES\t" + failures);
    }
}
