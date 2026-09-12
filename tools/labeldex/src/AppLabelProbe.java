import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.Looper;

import java.io.PrintStream;

/**
 * AdbManager 应用名读取工具（原创实现，无第三方代码）。
 *
 * 原理：Android 平台并不通过 adb 直接暴露「应用显示名」，dumpsys 只返回 labelRes
 * 资源 ID（部分 ROM 上甚至统一返回占位值，不可用）。APK 内的 label 只有系统
 * PackageManager 能正确解析（含本地化、资源引用、多语言回退）。
 *
 * 本工具借助 app_process 在设备上启动一个临时 Java 进程，通过反射拿到
 * system 级 Context，再调用 PackageManager.getApplicationLabel()，从而得到
 * 与系统桌面完全一致的应用名（含中文）。
 *
 * 重要：ActivityThread 属于隐藏 API，公开 android.jar 中不存在，
 * 因此这里必须全部使用反射，不能直接 import。
 *
 * 用法（设备端）：
 *   CLASSPATH=/data/local/tmp/adbmgr_label.dex app_process /system/bin AppLabelProbe <pkg> [pkg...]
 * 输出（每行）：
 *   <包名>\t<应用名>
 * 取不到时输出：
 *   <包名>\t<ERR> <原因>
 */
public final class AppLabelProbe {

    private AppLabelProbe() {
    }

    public static void main(String[] args) {
        // systemMain() 需要主 Looper，否则内部会抛 RuntimeException
        if (Looper.getMainLooper() == null) {
            Looper.prepareMainLooper();
        }

        // systemMain() 启动过程中会向 stderr 打一堆无用警告，先临时重定向丢弃
        PrintStream originalErr = System.err;
        try {
            System.setErr(new PrintStream("/dev/null"));
        } catch (Throwable ignored) {
            // 无法重定向时继续，只是输出会吵一些
        }

        Context context;
        try {
            Class<?> activityThread = Class.forName("android.app.ActivityThread");
            Object thread = activityThread.getMethod("systemMain").invoke(null);
            context = (Context) activityThread.getMethod("getSystemContext").invoke(thread);
        } catch (Throwable t) {
            System.setErr(originalErr);
            System.out.println("FATAL\t" + t);
            return;
        } finally {
            System.setErr(originalErr);
        }

        PackageManager pm = context.getPackageManager();
        for (String pkg : args) {
            if (pkg == null || pkg.isEmpty()) {
                continue;
            }
            try {
                ApplicationInfo info = pm.getApplicationInfo(pkg, 0);
                CharSequence label = pm.getApplicationLabel(info);
                // 去掉可能存在的制表符/换行，保证一包一行且字段可用 \t 切分
                String text = label == null ? "" : label.toString()
                        .replace('\t', ' ').replace('\n', ' ').replace('\r', ' ');
                System.out.println(pkg + "\t" + text);
            } catch (Throwable t) {
                System.out.println(pkg + "\t<ERR>");
            }
        }
    }
}
