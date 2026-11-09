using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DepalAdmin.Feature.Process;

/// <summary>
/// 환경 센서 그래프 1개 (온도 / 습도 / 가스). sensor/env 의 metric 하나를 맡는다.
/// 최근 WindowMinutes분 값만 들고 있다 (지난 기록은 DB environment_readings).
/// </summary>
public partial class EnvSeries : ObservableObject
{
    public EnvSeries(string metric, string title, string unit, string format, double warnLevel, double minSpan, string warnNote)
    {
        Metric = metric;
        Title = title;
        Unit = unit;
        Format = format;
        WarnLevel = warnLevel;
        MinSpan = minSpan;
        WarnNote = warnNote;
    }

    /// <summary>sensor/env readings[].metric 값 (GAS / TEMPERATURE / HUMIDITY)</summary>
    public string Metric { get; }
    public string Title { get; }
    public string Unit { get; }
    public string Format { get; }
    /// <summary>이 값 이상이면 경고 (⚠ 임시 기준 — 하드웨어 담당 실측 후 확정)</summary>
    public double WarnLevel { get; }
    public double MinSpan { get; }
    /// <summary>그래프 아래 안내 (예: "경고 35℃ 이상 · 임시")</summary>
    public string WarnNote { get; }

    public ObservableCollection<TrendPoint> Points { get; } = new();

    [ObservableProperty] private string _currentText = "-";
    [ObservableProperty] private string _rangeText = "";
    /// <summary>ok / warn / stale (값이 한동안 안 옴)</summary>
    [ObservableProperty] private string _status = "stale";
    [ObservableProperty] private string _statusText = "값 없음";

    public DateTime? LastAt { get; private set; }

    public void Add(DateTime time, double value, double windowMinutes)
    {
        Points.Add(new TrendPoint(time, value));
        var from = time.AddMinutes(-windowMinutes);
        while (Points.Count > 0 && Points[0].Time < from) Points.RemoveAt(0);

        LastAt = time;
        CurrentText = value.ToString(Format, CultureInfo.InvariantCulture);
        var lo = Points.Min(p => p.Value);
        var hi = Points.Max(p => p.Value);
        RangeText = $"최근 {windowMinutes:0}분  {lo.ToString(Format, CultureInfo.InvariantCulture)} ~ {hi.ToString(Format, CultureInfo.InvariantCulture)}{Unit}";
        UpdateStatus(time, staleSeconds: double.MaxValue);
    }

    /// <summary>1초마다: 값이 staleSeconds초 넘게 안 오면 "끊김"</summary>
    public void UpdateStatus(DateTime now, double staleSeconds)
    {
        if (LastAt is not { } last)
        {
            Status = "stale";
            StatusText = "값 없음";
            return;
        }
        if ((now - last).TotalSeconds > staleSeconds)
        {
            Status = "stale";
            StatusText = $"{(int)(now - last).TotalSeconds}초째 값 없음";
            return;
        }
        var warn = Points.Count > 0 && Points[^1].Value >= WarnLevel;
        Status = warn ? "warn" : "ok";
        StatusText = warn ? "기준 초과" : "정상";
    }
}
