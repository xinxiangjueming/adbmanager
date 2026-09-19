using AdbManager.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager.Ui;

/// <summary>
/// 应用名 / 图标「流式补全」的通用编排（进度胶囊 + 代次作废 + 两轮串行），
/// 应用页与进程页共用，避免两处各写一份并发逻辑而走样。
///
/// 封装三条并发约束：
/// 1. 过期回调用**代次号**判定，不能用设备序列号——设备没换但重开了一轮时，上一轮的回调必须失效。
/// 2. 代次号在等待上一轮收尾**之前**递增。若先等再递增，上一轮尚在排队的收尾回调会把刚启动的
///    新一轮误标为已结束（收尾回调经 DispatcherQueue 入队，任务本身会先于它完成）。
/// 3. 两轮必须串行。批内没有取消点（一批就是一次 app_process 调用），并发两次会在设备端
///    互相清理图标目录，破坏 tar 打包。
/// </summary>
public sealed class AppInfoStream
{
    private readonly ProgressRing _ring = new() { IsActive = true, Width = 16, Height = 16 };
    private readonly TextBlock _text = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    private readonly Func<DispatcherQueue?> _queueProvider;
    private readonly Func<string, bool> _isTargetCurrent;
    private readonly Action<AppInfoUpdate> _apply;
    private readonly Action _onFinished;

    private Task _task = Task.CompletedTask;
    private int _generation;
    private string _serial = "";

    /// <param name="queueProvider">取当前页面的 DispatcherQueue。</param>
    /// <param name="isTargetCurrent">该设备是否仍是界面当前展示的设备。</param>
    /// <param name="apply">回填一批结果；保证在 UI 线程、且仅在结果仍有效时调用。</param>
    /// <param name="onFinished">本轮结束（正常或异常）回调，已在 UI 线程。</param>
    public AppInfoStream(
        Func<DispatcherQueue?> queueProvider,
        Func<string, bool> isTargetCurrent,
        Action<AppInfoUpdate> apply,
        Action onFinished)
    {
        _queueProvider = queueProvider;
        _isTargetCurrent = isTargetCurrent;
        _apply = apply;
        _onFinished = onFinished;

        _ring.Margin = new Thickness(0, 0, 8, 0);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_ring);
        row.Children.Add(_text);

        Chip = new Border
        {
            Child = row,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(12),
            Background = Miuix.Brush("MiuixCardBackground"),
            BorderBrush = Miuix.Brush("MiuixDivider"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 8, 8),
            Visibility = Visibility.Collapsed
        };
    }

    /// <summary>右下角悬浮的进度胶囊，调用方把它叠放进列表所在的 Grid（同格叠放不占布局）。</summary>
    public Border Chip { get; }

    /// <summary>补全进行中。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>上次补全被掉线打断，需要重新进入页面时补跑一次。</summary>
    public bool NeedsRetry { get; private set; }

    /// <summary>
    /// 启动一轮。上一轮未结束则先等它收尾再重开（见类型注释第 3 条）。
    /// </summary>
    /// <param name="serial">目标设备。</param>
    /// <param name="packageNames">取当前需要补全的包名；在调用线程上求值。</param>
    public async Task StartAsync(string serial, Func<IReadOnlyList<string>> packageNames)
    {
        // 先递增代次：上一轮尚在排队的收尾回调会因代次不匹配被丢弃（见类型注释第 2 条）
        var generation = ++_generation;

        if (IsRunning) await _task;

        IsRunning = true;
        NeedsRetry = false;
        _serial = serial;
        _text.Text = LocalizationService.Get("Apps_InfoLoading");
        _ring.IsActive = true;
        Chip.Visibility = Visibility.Visible;

        _task = RunAsync(serial, generation, packageNames);
    }

    /// <summary>设备掉线：停止显示并记下「需补跑」，等重新连上后由页面触发。</summary>
    public void Abort()
    {
        if (!IsRunning) return;

        NeedsRetry = true;
        _generation++; // 让在途回调与排队的收尾失效
        IsRunning = false;
        _ring.IsActive = false;
        Chip.Visibility = Visibility.Collapsed;
    }

    private async Task RunAsync(string serial, int generation, Func<IReadOnlyList<string>> packageNames)
    {
        try
        {
            // 在调用线程（UI 线程）取包名快照，避免后台线程遍历正在变动的集合
            var names = packageNames();
            await AppState.Adb.LoadAppInfoStreamingAsync(
                serial, names, update => Post(() => Apply(serial, generation, update)));
        }
        catch (Exception ex)
        {
            AppState.Log.Error(LocalizationService.Get("Apps_Err_LabelRead", ex.Message));
        }
        finally
        {
            Post(() => Finish(serial, generation));
        }
    }

    private void Apply(string serial, int generation, AppInfoUpdate update)
    {
        if (generation != _generation) return;
        if (!_isTargetCurrent(serial)) return;

        _apply(update);

        // 进度只在真正联网解析的批次上有意义（缓存命中阶段没有进度可言）
        _text.Text = update.Stage == AppInfoStage.Cached
            ? LocalizationService.Get("Apps_InfoLoading")
            : LocalizationService.Get("Apps_InfoLoading") + $"  {update.Done}/{update.Total}";
    }

    private void Finish(string serial, int generation)
    {
        if (generation != _generation) return;
        if (!string.Equals(_serial, serial, StringComparison.Ordinal)) return;

        IsRunning = false;
        NeedsRetry = false;
        _ring.IsActive = false;
        Chip.Visibility = Visibility.Collapsed;
        _onFinished();
    }

    private void Post(Action action)
    {
        var queue = _queueProvider();
        if (queue is { } q && !q.HasThreadAccess) q.TryEnqueue(() => action());
        else action();
    }
}
