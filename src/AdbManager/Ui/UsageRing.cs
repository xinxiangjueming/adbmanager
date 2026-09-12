using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AdbManager.Ui;

/// <summary>
/// 占比环：圆形进度环（0~100），中心显示百分比。
/// 纯 Path 绘制，配色取 miuix 画笔随深浅色主题自动切换；
/// 占用 ≥85% 时环变红色警示。
/// </summary>
public sealed class UsageRing : Grid
{
    private const double StrokeWidth = 8;

    private readonly Path _track = new();
    private readonly Path _value = new()
    {
        StrokeThickness = StrokeWidth,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };
    private readonly TextBlock _text = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Miuix.Brush("MiuixTextPrimary")
    };

    private readonly double _diameter;

    public UsageRing(double diameter = 84)
    {
        _diameter = diameter;
        Width = Height = diameter;

        _track.Stroke = Miuix.Brush("MiuixCardBorder");
        _track.StrokeThickness = StrokeWidth;
        _value.Stroke = Miuix.Brush("MiuixAccent");
        _text.FontSize = diameter * 0.24;

        Children.Add(_track);
        Children.Add(_value);
        Children.Add(_text);

        Set(0);
    }

    /// <summary>设置占比（0~100）。percent 为 NaN 等非法值时按 0 处理。</summary>
    public void Set(double percent)
    {
        var clamped = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        _text.Text = $"{Math.Round(clamped):0}%";
        _value.Stroke = Miuix.Brush(clamped >= 85 ? "MiuixDanger" : "MiuixAccent");

        var radius = (_diameter - StrokeWidth) / 2.0;
        var center = _diameter / 2.0;

        // 底环：完整圆
        _track.Data = new EllipseGeometry
        {
            Center = new Windows.Foundation.Point(center, center),
            RadiusX = radius,
            RadiusY = radius
        };

        if (clamped <= 0)
        {
            _value.Data = null;
            return;
        }

        if (clamped >= 100)
        {
            // 满环直接画整圆，避免 ArcSegment 起止点重合退化
            _value.Data = new EllipseGeometry
            {
                Center = new Windows.Foundation.Point(center, center),
                RadiusX = radius,
                RadiusY = radius
            };
            return;
        }

        // 从 12 点方向顺时针扫过 percent 对应的弧
        var sweep = clamped / 100.0 * 2 * Math.PI;
        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(center, center - radius),
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(
                center + radius * Math.Cos(sweep - Math.PI / 2),
                center + radius * Math.Sin(sweep - Math.PI / 2)),
            Size = new Windows.Foundation.Size(radius, radius),
            IsLargeArc = clamped > 50,
            SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _value.Data = geometry;
    }
}
