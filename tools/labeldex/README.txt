# labeldex —— 免 root 读取应用名与图标（app_process + dex）
#
# 用途：ADB 工具在设备端（未 root、无 aapt）取"应用名 + 图标"。
# 本项目（Windows-adb / AdbManager，C# WinUI 3）与 D:/wearadb（Kotlin / Wear OS）共用同一份 dex。
#
# ─────────────────────────────────────────────────────────────
# 文件说明
#   AppInfoProbe.java   ← **当前使用中**：一次取回 应用名 + 图标（合并版，支持 -s 缩放）
#   AppLabelProbe.java  ← 早期版本：只取应用名（保留作参考）
#   AppIconProbe.java   ← 早期版本：只取图标（保留作参考）
#   build-dex.ps1       ← 构建脚本（JBR 的 javac 在部分沙箱不可执行，需真实终端验证）
#
# ─────────────────────────────────────────────────────────────
# 构建（AppInfoProbe）
#   mkdir -p build/classes build/dex
#   cp /e/Sdk/platforms/android-36/android.jar ./android.jar      # Git Bash 下必须 cp 到本地：
#                                                                 # -classpath /e/Sdk/... 会被转义破坏
#   "C:/Users/kongj/jdk-17.0.20+8/bin/javac.exe" -encoding UTF-8 -source 11 -target 11 \
#       -nowarn -classpath android.jar -d build/classes src/AppInfoProbe.java
#   "C:/Users/kongj/jdk-17.0.20+8/bin/java.exe" -cp "E:/Sdk/build-tools/36.0.0/lib/d8.jar" \
#       com.android.tools.r8.D8 --min-api 24 --output build/dex build/classes/AppInfoProbe.class
#
#   产物：build/dex/classes.dex（5096 字节；含 label-only 模式）
#
# ─────────────────────────────────────────────────────────────
# 运行
#   adb push build/dex/classes.dex /data/local/tmp/<app>/appinfo.dex
#
#   # 名称 + 图标
#   adb shell "CLASSPATH=<dex> app_process /system/bin AppInfoProbe <outDir> -s 96 <pkg...>"
#
#   # 只要名称（label-only）：outDir 传 '-'，跳过 PNG 编码与磁盘写入
#   adb shell "CLASSPATH=<dex> app_process /system/bin AppInfoProbe - <pkg...>"
#
#   输出协议（每行）：包名 \t 应用名 \t 图标PNG路径
#     名称失败 → <ERR>；图标失败 → 路径为空；label-only 模式第三列恒为空
#   -s <size> 指定图标目标边长（默认 96），等比缩放、只缩不放
#
# ─────────────────────────────────────────────────────────────
# 关键实现点
#   1. ActivityThread 是隐藏 API，必须纯反射（Class.forName + systemMain + getSystemContext）
#   2. systemMain().getSystemContext() 拿到的 Context 已能按当前用户解析全部包（含第三方），
#      无需额外 createContextAsUser / LauncherApps（实测都不如它可靠）
#   3. 取图标：createPackageContext(pkg, CONTEXT_IGNORE_SECURITY)
#      → getResources().getDrawableForDensity(info.icon, 密度)
#      逐档尝试 320/240/213/160/480/640，取到即用；失败回退 info.loadIcon(pm)
#   4. Drawable → Bitmap → PNG（ARGB_8888，quality 100）
#   5. 尺寸不定（实测 48px~3600px，60+ 种），不缩放会拖垮内存与布局；
#      -s 96 后尺寸收敛到 9 种、体积 7.9MB → 3.0MB
#
# ─────────────────────────────────────────────────────────────
# 实测基线（22081212C）
#   全量 395 包：名称 0 失败、图标 0 失败、3.6 秒（含 JVM 启动 ~2.8s）
#   96px 图标 3.0MB → tar -czf 后 1.15MB → pull 0.04s
#
# ─────────────────────────────────────────────────────────────
# 坑位速查
#   • ActivityThread 隐藏 API → 纯反射，不能 import（公开 android.jar 无此类）
#   • javac 必须 -encoding UTF-8 + -source/-target 11（JDK21 默认 class 69 会被 d8 拒）
#   • Git Bash 下 d8.bat 参数被转义破坏 → 直调 java -cp d8.jar com.android.tools.r8.D8
#   • adb push/pull 必须用 Windows 路径（D:/...），Git Bash 的 /tmp 会造成找不到文件
#   • 先确认设备身份（adb devices -l）——不同设备包数差异大（实测 395 vs 570），
#     查不到的包可能只是那台设备上没装
#
# ─────────────────────────────────────────────────────────────
# 跨项目复用
#   D:/wearadb/app/src/main/assets/appinfo.dex 是本 dex 的副本（2026-09-10 起）。
#   dex 内容变更时需同步更新该副本，并递增 WearAdb 侧 AppInfoResolver 的 DEX_VERSION。
