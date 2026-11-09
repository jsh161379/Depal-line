using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DepalAdmin.Feature.Process;

/// <summary>그래프 점 하나 (시각, 값)</summary>
public readonly record struct TrendPoint(DateTime Time, double Value);

/// <summary>
/// 작은 시간 추이 그래프 (패키지 없이 직접 그림).
/// Points(TrendPoint 목록)가 바뀔 때마다 다시 그린다. 가로축 = 최근 WindowMinutes분, 오른쪽 끝 = 지금.
/// WarnLevel을 주면 빨간 점선으로 경고 기준을 그린다.
/// 색은 Common.xaml 키(Brush.Field, Brush.RuleSoft, Brush.SubText, Brush.Alarm)를 찾아 쓴다.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarnLevelProperty = DependencyProperty.Register(
        nameof(WarnLevel), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinSpanProperty = DependencyProperty.Register(
        nameof(MinSpan), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowMinutesProperty = DependencyProperty.Register(
        nameof(WindowMinutes), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelFormatProperty = DependencyProperty.Register(
        nameof(LabelFormat), typeof(string), typeof(TrendChart),
        new FrameworkPropertyMetadata("0.0", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>TrendPoint 목록 (ObservableCollection이면 추가·삭제 때 자동으로 다시 그림)</summary>
    public IEnumerable? Points { get => (IEnumerable?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>경고 기준값 (NaN이면 안 그림)</summary>
    public double WarnLevel { get => (double)GetValue(WarnLevelProperty); set => SetValue(WarnLevelProperty, value); }
    /// <summary>세로축 최소 폭 (값이 거의 안 변해도 선이 요동쳐 보이지 않게)</summary>
    public double MinSpan { get => (double)GetValue(MinSpanProperty); set => SetValue(MinSpanProperty, value); }
    public double WindowMinutes { get => (double)GetValue(WindowMinutesProperty); set => SetValue(WindowMinutesProperty, value); }
    /// <summary>세로축 숫자 형식 (예: "0.0", "0")</summary>
    public string LabelFormat { get => (string)GetValue(LabelFormatProperty); set => SetValue(LabelFormatProperty, value); }

    private const double PadLeft = 40, PadRight = 8, PadTop = 6, PadBottom = 16;

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if (e.OldValue is INotifyCollectionChanged oldList) oldList.CollectionChanged -= chart.OnListChanged;
        if (e.NewValue is INotifyCollectionChanged newList) newList.CollectionChanged += chart.OnListChanged;
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 110 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < PadLeft + PadRight + 10 || h < PadTop + PadBottom + 10) return;

        var field = Res("Brush.Field", Brushes.WhiteSmoke);
        var rule = Res("Brush.RuleSoft", Brushes.LightGray);
        var sub = Res("Brush.SubText", Brushes.Gray);
        var alarm = Res("Brush.Alarm", Brushes.Firebrick);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);
        dc.DrawRectangle(field, new Pen(rule, 1), plot);

        var now = DateTime.Now;
        var window = TimeSpan.FromMinutes(Math.Max(1, WindowMinutes));
        var from = now - window;
        var pts = (Points ?? Array.Empty<TrendPoint>()).OfType<TrendPoint>().Where(p => p.Time >= from).ToList();

        // 가로축 글자: -10분 · -5분 · 지금
        Text(dc, $"-{WindowMinutes:0}분", plot.Left, plot.Bottom + 2, sub, dpi, 10, TextAlignment.Left);
        Text(dc, $"-{WindowMinutes / 2:0.#}분", plot.Left + plot.Width / 2, plot.Bottom + 2, sub, dpi, 10, TextAlignment.Center);
        Text(dc, "지금", plot.Right, plot.Bottom + 2, sub, dpi, 10, TextAlignment.Right);

        if (pts.Count == 0)
        {
            Text(dc, "값 없음", plot.Left + plot.Width / 2, plot.Top + plot.Height / 2 - 7, sub, dpi, 12, TextAlignment.Center);
            return;
        }

        // 세로축 범위: 값 + 경고 기준(있으면), 위아래 10% 여유, 최소 폭 MinSpan
        var lo = pts.Min(p => p.Value);
        var hi = pts.Max(p => p.Value);
        if (!double.IsNaN(WarnLevel)) { lo = Math.Min(lo, WarnLevel); hi = Math.Max(hi, WarnLevel); }
        var span = Math.Max(hi - lo, MinSpan);
        var mid = (hi + lo) / 2;
        lo = mid - span / 2 - span * 0.1;
        hi = mid + span / 2 + span * 0.1;

        double X(DateTime t) => plot.Left + plot.Width * (t - from).TotalMilliseconds / window.TotalMilliseconds;
        double Y(double v) => plot.Bottom - plot.Height * (v - lo) / (hi - lo);

        // 가로 눈금 3줄 + 세로축 숫자
        var gridPen = new Pen(rule, 0.6);
        for (var i = 0; i <= 2; i++)
        {
            var v = lo + (hi - lo) * i / 2;
            var y = Y(v);
            if (i == 1) dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(dc, v.ToString(LabelFormat, CultureInfo.InvariantCulture), plot.Left - 4, y - 7, sub, dpi, 10, TextAlignment.Right);
        }

        // 경고 기준 (빨간 점선)
        if (!double.IsNaN(WarnLevel))
        {
            var y = Y(WarnLevel);
            var warnPen = new Pen(alarm, 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(warnPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // 값 선
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(pts[0].Time), Y(pts[0].Value)), false, false);
            for (var i = 1; i < pts.Count; i++)
                g.LineTo(new Point(X(pts[i].Time), Y(pts[i].Value)), true, true);
        }
        geo.Freeze();
        dc.PushClip(new RectangleGeometry(plot));
        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round }, geo);

        // 마지막 점 (경고 넘으면 빨강)
        var last = pts[^1];
        var lastBrush = !double.IsNaN(WarnLevel) && last.Value >= WarnLevel ? alarm : Stroke;
        dc.DrawEllipse(lastBrush, null, new Point(X(last.Time), Y(last.Value)), 3.2, 3.2);
        dc.Pop();
    }

    private void Text(DrawingContext dc, string s, double x, double y, Brush brush, double dpi, double size, TextAlignment align)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), size, brush, dpi) { TextAlignment = align };
        dc.DrawText(ft, new Point(x, y));
    }
}
