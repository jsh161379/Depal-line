using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DepalAdmin.Core.Alarms;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;

namespace DepalAdmin.Feature.Process;

/// <summary>
/// 담당: 조시현 — 라인 현황 아래 "환경 센서" 칸 (온도 · 습도 그래프 + 가스 감시).
/// 받는 것: sensor/env (메시지_형식 v1.3.1 5장). 센서는 Mega가 읽고 PC 중계 프로그램이 MQTT로 발행.
///
///   온습도: 30초마다 1건 → 최근 30분 그래프. 75초 넘게 안 오면 "값 없음"
///   가스  : 평소엔 대기. 감지되면 카드가 빨갛게 + 알람(아래 알람 줄). 해제되면 기록만 남김
///           value 해석 — unit이 "0/1"이면 1 = 감지 (MQ-9 DO 핀)
///                         그 밖(raw 원시값)이면 GasRawThreshold 이상 = 감지
///
/// ⚠ 경고 기준값은 임시. 하드웨어 담당이 실측 후 정하면 아래 숫자만 바꾸면 된다.
/// </summary>
public partial class EnvMonitorViewModel : ObservableObject
{
    // ── 수정할 값 ──────────────────────────────────────────
    private const double TempHumWindowMinutes = 30;  // 그래프에 보여 줄 시간
    private const double TempHumStaleSeconds = 75;   // 30초 주기 → 두 번 연속 빠지면 "값 없음"
    private const double TempWarn = 35;              // ℃ 이상이면 경고 (임시)
    private const double HumWarn = 70;               // % 이상이면 경고 (임시)
    private const double GasRawThreshold = 400;      // raw로 올 때 이 값 이상 = 감지 (임시, DO 방식이면 안 씀)
    private const double GasSilenceSeconds = 150;    // 가스 신호가 이만큼 안 오면 "신호 없음" 주의 (아두이노가 60초마다 살아 있음을 보낼 때)
    private const int GasLogMax = 20;
    private const string AlarmDevice = "mega";       // 알람 줄에 표시할 장치 (센서 → Mega → PC 중계)
    // ──────────────────────────────────────────────────────

    private readonly IAlarmService _alarms;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, bool> _warned = new();

    public EnvSeries Temperature { get; } =
        new(EnvMetrics.Temperature, "온도", "℃", "0.0", warnLevel: TempWarn, minSpan: 2, warnNote: $"경고 {TempWarn}℃ 이상 (임시)");

    public EnvSeries Humidity { get; } =
        new(EnvMetrics.Humidity, "습도", "%", "0", warnLevel: HumWarn, minSpan: 5, warnNote: $"경고 {HumWarn}% 이상 (임시)");

    /// <summary>가스 감지 기록 (최신이 위)</summary>
    public ObservableCollection<GasLogItem> GasLog { get; } = new();

    [ObservableProperty] private bool _gasDetected;
    /// <summary>idle(대기·정상) / alarm(감지) / stale(신호 없음)</summary>
    [ObservableProperty] private string _gasStatus = "stale";
    [ObservableProperty] private string _gasStatusText = "신호 없음";
    [ObservableProperty] private string _gasDetailText = "아직 가스 센서 신호를 받지 못했습니다";
    [ObservableProperty] private string _gasValueText = "-";
    [ObservableProperty] private string _summary = "센서 값 없음";

    private DateTime? _gasLastSignal;
    private DateTime? _gasSince;

    public EnvMonitorViewModel(ILineClient client, IAlarmService alarms)
    {
        _alarms = alarms;
        client.EnvReceived += (_, env) => Apply(env);
        if (client.LastEnv != null) Apply(client.LastEnv);

        _clock.Tick += (_, _) => Tick();
        _clock.Start();
    }

    // ── sensor/env 1건 ──────────────────────────────────

    private void Apply(EnvReading env)
    {
        // 받은 시각 기준 (PC 중계 시계가 조금 틀려도 그래프가 밀리지 않게). 측정 시각 env.Ts는 DB에 저장됨
        var now = DateTime.Now;

        foreach (var series in new[] { Temperature, Humidity })
        {
            var v = env.Find(series.Metric);
            if (v == null) continue;
            series.Add(now, v.Value, TempHumWindowMinutes);
            WarnOnce(series.Metric, v.Value >= series.WarnLevel,
                $"{series.Title} {v.Value.ToString(series.Format, CultureInfo.InvariantCulture)}{series.Unit} — 기준 {series.WarnLevel}{series.Unit} 이상");
        }

        var gas = env.Find(EnvMetrics.Gas);
        if (gas != null) ApplyGas(gas, now);

        Tick();
    }

    private void ApplyGas(EnvValue gas, DateTime now)
    {
        _gasLastSignal = now;
        var digital = gas.Unit == "0/1";
        var detected = digital ? gas.Value >= 0.5 : gas.Value >= GasRawThreshold;
        GasValueText = digital ? (detected ? "감지" : "없음") : $"{gas.Value:0} raw";

        if (detected && !GasDetected)
        {
            GasDetected = true;
            _gasSince = now;
            AddGasLog(now, "감지", "alarm");
            _alarms.Raise(AlarmDevice, "ERROR", $"가스 감지 ({gas.Sensor})");
        }
        else if (!detected && GasDetected)
        {
            GasDetected = false;
            var secs = _gasSince is { } since ? (int)(now - since).TotalSeconds : 0;
            AddGasLog(now, $"해제 ({secs}초 동안)", "info");
            _gasSince = null;
        }
    }

    private void AddGasLog(DateTime time, string text, string level)
    {
        GasLog.Insert(0, new GasLogItem(time.ToString("HH:mm:ss"), text, level));
        while (GasLog.Count > GasLogMax) GasLog.RemoveAt(GasLog.Count - 1);
    }

    /// <summary>기준을 넘는 순간에만 알람 1건 (넘어 있는 동안 반복하지 않음)</summary>
    private void WarnOnce(string key, bool over, string message)
    {
        var was = _warned.TryGetValue(key, out var w) && w;
        if (over && !was) _alarms.Raise(AlarmDevice, "WARN", message);
        _warned[key] = over;
    }

    // ── 1초마다: 끊김 · 경과 표시 ───────────────────────────

    private void Tick()
    {
        var now = DateTime.Now;
        Temperature.UpdateStatus(now, TempHumStaleSeconds);
        Humidity.UpdateStatus(now, TempHumStaleSeconds);

        if (GasDetected)
        {
            GasStatus = "alarm";
            GasStatusText = "가스 감지!";
            GasDetailText = _gasSince is { } since
                ? $"{since:HH:mm:ss}부터 {(int)(now - since).TotalSeconds}초째 — 환기·현장 확인"
                : "환기·현장 확인";
        }
        else if (_gasLastSignal is not { } last || (now - last).TotalSeconds > GasSilenceSeconds)
        {
            GasStatus = "stale";
            GasStatusText = "신호 없음";
            GasDetailText = _gasLastSignal is { } l
                ? $"마지막 신호 {Ago(now - l)} 전 — Mega USB 선·중계 프로그램 확인"
                : "아직 가스 센서 신호를 받지 못했습니다";
        }
        else
        {
            GasStatus = "idle";
            GasStatusText = "대기 중 · 정상";
            GasDetailText = $"마지막 신호 {Ago(now - last)} 전";
        }

        var warn = new List<string>();
        if (GasDetected) warn.Add("가스 감지");
        if (Temperature.Status == "warn") warn.Add("온도 높음");
        if (Humidity.Status == "warn") warn.Add("습도 높음");
        Summary = warn.Count > 0 ? string.Join(" · ", warn)
            : Temperature.Status == "stale" && Humidity.Status == "stale" && GasStatus == "stale" ? "센서 값 없음"
            : "정상";
    }

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{Math.Max(0, (int)t.TotalSeconds)}초";
}

/// <summary>가스 감지 기록 한 줄. Level: alarm(감지, 빨강) / info(해제, 검정)</summary>
public sealed record GasLogItem(string Time, string Text, string Level);
