using System.Windows.Threading;
using DepalAdmin.Core;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;

namespace DepalAdmin.Comm;

/// <summary>
/// 브로커 없이 화면을 만들 수 있게 1초마다 가짜 데이터를 만든다.
/// settings.json에서 "UseFakeMqtt": true 일 때 쓰인다.
/// 알람 화면 시험용으로 가끔 장치 지연(WARN)과 집기 실패(ERROR → 일시정지)도 일으킨다.
/// ⚠ 등급 판정은 화면 개발용 흉내일 뿐. 실제 판정은 라인 컨트롤러가 한다 (기준값 임시).
/// </summary>
public sealed class FakeLineClient : LineClientBase
{
    // ── 수정할 값 ──────────────────────────────────────────
    private const int PalletRows = 2, PalletCols = 4;   // 팔레트 한 판 8개 = 2줄 × 4칸 (11/20 변경, 규약 v1.5.1)
    private const int BatteriesPerRun = PalletRows * PalletCols;
    private const int AgvBatch = 8;               // 잠정값
    private const double DelayChance = 0.01;      // 1초마다 어떤 장치가 '지연'될 확률
    private const int DelaySeconds = 4;
    private const double PickFailChance = 0.06;   // 집기 실패 확률
    private const int TempHumEverySeconds = 30;   // 온습도 발행 주기 (아두이노 30초마다)
    private const int GasAliveEverySeconds = 60;  // 가스: 상태가 그대로여도 이 주기로 "살아 있음" 1건
    private const double GasStartChance = 0.008;  // 1초마다 가스가 감지될 확률 (경고 화면 시험용)
    // ──────────────────────────────────────────────────────

    private static readonly string[] Steps = { "scan", "pick", "inspect", "measure", "home", "sort" };
    private static readonly string[] DeviceNames = { "server", "controller", "arm1", "mega", "yolo", "agv", "env", "db_saver" };
    private static readonly string[] AgvCycle = { "going", "going", "away", "away", "returning", "returning", "home" };

    private readonly Random _rnd = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, int> _counts = Grades.All.ToDictionary(g => g, _ => 0);
    private readonly Dictionary<string, int> _delayLeft = new();

    private string _mode = "idle";
    private string? _step;
    private string? _runId;
    private int _batteryId;
    private int _stepIndex;
    private int[] _cell = { 0, 0 };
    private int _agvLoaded;
    private int _agvCycleIndex = AgvCycle.Length - 1;   // home
    private int _envTick;
    private double _temp = 24.0, _hum = 42.0;
    private int _gasLeft;          // 가스 감지가 남은 초 (0 = 없음)
    private bool _gasSent;         // 마지막으로 보낸 가스 상태
    private string? _message = "가짜 데이터 모드입니다. 라인 현황에서 [시작]을 눌러 보세요.";

    public FakeLineClient()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public override Task StartAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Fake);
        RaiseDevices(MakeDevices());
        RaiseLine(MakeLine());
        RaiseEnv(MakeTempHum());
        RaiseEnv(MakeGas(false));
        _timer.Start();
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        OnUi(() => _timer.Stop());
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public override Task SendCommandAsync(string action, CancellationToken ct = default)
    {
        switch (action)
        {
            case UiActions.Start when _mode == "idle":
                StartRun();
                break;
            case UiActions.Pause when _mode == "running":
                _mode = "paused";
                _message = "작업자 일시정지";
                Event("INFO", "controller", "일시정지");
                break;
            case UiActions.Resume when _mode == "paused":
                _mode = "running";
                _message = null;
                Event("INFO", "controller", "재개");
                break;
            case UiActions.Stop when _mode is "running" or "paused":
                EndRun("작업자가 회차 종료");
                break;
            case UiActions.Estop:
                _mode = "estop";
                _message = "비상정지 상태 — 원인 확인 후 [비상정지 해제]";
                Event("ERROR", "controller", "비상정지");
                break;
            case UiActions.Reset when _mode == "estop":
                _mode = "idle";
                _step = null;
                _message = null;
                Event("INFO", "controller", "비상정지 해제");
                break;
            case UiActions.Skip when _mode == "paused" && _step == "wait_operator":
                Event("WARN", "controller", $"건전지 {_batteryId} 건너뜀");
                _stepIndex = 0;   // 다음 칸부터
                _mode = "running";
                _message = null;
                break;
            default:
                Event("WARN", "controller", $"지금 상태({_mode})에서는 '{action}' 명령을 받을 수 없음");
                break;
        }
        RaiseLine(MakeLine());
        return Task.CompletedTask;
    }

    // ── 1초마다 ────────────────────────────────────────────

    private void Tick()
    {
        AdvanceDelays();
        RaiseDevices(MakeDevices());
        AdvanceAgv();
        if (_mode == "running") AdvanceStep();
        RaiseLine(MakeLine());
        AdvanceEnv();
    }

    // ── sensor/env 흉내 (환경 Uno → PC 중계 env_relay → MQTT, 규약 v1.5) ─────────────
    //   온습도: 30초마다 / 가스: 상태가 바뀌면 바로 + 60초마다 살아 있음 (value 0/1)

    private void AdvanceEnv()
    {
        _envTick++;
        if (_envTick % TempHumEverySeconds == 0) RaiseEnv(MakeTempHum());

        if (_gasLeft == 0 && _rnd.NextDouble() < GasStartChance) _gasLeft = 6 + _rnd.Next(10);
        else if (_gasLeft > 0) _gasLeft--;
        var gas = _gasLeft > 0;
        if (gas != _gasSent || _envTick % GasAliveEverySeconds == 0) RaiseEnv(MakeGas(gas));
    }

    private EnvReading MakeTempHum()
    {
        _temp = Math.Clamp(_temp + (_rnd.NextDouble() - 0.5) * 0.6, 20, 30);
        _hum = Math.Clamp(_hum + (_rnd.NextDouble() - 0.5) * 2.0, 30, 60);
        return Env(
            new EnvValue { Sensor = "temp_hum_1", Metric = EnvMetrics.Temperature, Value = Math.Round(_temp, 1), Unit = "C" },
            new EnvValue { Sensor = "temp_hum_1", Metric = EnvMetrics.Humidity, Value = Math.Round(_hum, 1), Unit = "%" });
    }

    private EnvReading MakeGas(bool detected)
    {
        _gasSent = detected;
        return Env(new EnvValue { Sensor = "gas_1", Metric = EnvMetrics.Gas, Value = detected ? 1 : 0, Unit = "0/1" });
    }

    private EnvReading Env(params EnvValue[] values)
    {
        var env = new EnvReading { ReadingId = Guid.NewGuid().ToString("N"), RunId = _runId, Ts = Json.Now(), Device = "env" };
        env.Readings.AddRange(values);
        return env;
    }

    private void AdvanceStep()
    {
        _stepIndex = (_stepIndex + 1) % Steps.Length;
        _step = Steps[_stepIndex];
        switch (_step)
        {
            case "scan":
                if (_batteryId >= BatteriesPerRun) EndRun("팔레트 비었음");
                break;
            case "pick":
                _batteryId++;
                _cell = new[] { (_batteryId - 1) / PalletCols % PalletRows, (_batteryId - 1) % PalletCols };
                if (_rnd.NextDouble() < PickFailChance)
                {
                    _mode = "paused";
                    _step = "wait_operator";
                    _message = $"건전지 {_batteryId} 집기 실패 — [재개]로 다시 시도하거나 [건너뛰기]";
                    Event("ERROR", "arm1", $"pick 실패 (grip_failed) 칸 [{_cell[0]},{_cell[1]}]");
                }
                break;
            case "sort":
                FinishBattery();
                break;
        }
    }

    private void AdvanceDelays()
    {
        foreach (var name in _delayLeft.Keys.ToList())
        {
            if (--_delayLeft[name] > 0) continue;
            _delayLeft.Remove(name);
            Event("INFO", name, "복구됨 (지연 → 정상)");
        }

        if (_rnd.NextDouble() < DelayChance)
        {
            var name = DeviceNames[_rnd.Next(1, DeviceNames.Length)];   // server 빼고
            if (_delayLeft.TryAdd(name, DelaySeconds))
                Event("WARN", name, $"하트비트 3.{_rnd.Next(10)}초 없음 → 지연");
        }
    }

    private void AdvanceAgv()
    {
        if (AgvCycle[_agvCycleIndex] == "home") return;
        _agvCycleIndex++;
        if (AgvCycle[_agvCycleIndex] == "away") Event("INFO", "agv", "AGV 목적지 도착, 하역");
        if (AgvCycle[_agvCycleIndex] == "home") Event("INFO", "agv", "AGV 복귀 완료");
    }

    private void StartRun()
    {
        _runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{_rnd.Next(0x10000):x4}";
        foreach (var key in _counts.Keys.ToList()) _counts[key] = 0;
        _batteryId = 0;
        _stepIndex = 0;
        _step = Steps[0];
        _mode = "running";
        _message = null;
        Event("INFO", "controller", "회차 시작");
    }

    private void EndRun(string reason)
    {
        if (_agvLoaded > 0) SendAgv();   // 회차 끝: 남은 A도 출발 (팀 결정)
        _mode = "idle";
        _step = null;
        _message = $"회차 종료 — {reason}";
        Event("INFO", "controller", $"회차 종료 ({reason})");
    }

    private void FinishBattery()
    {
        var result = MakeResult();
        _counts[result.Grade]++;
        RaiseResult(result);

        if (result.Dest == "AGV")
        {
            _agvLoaded++;
            if (_agvLoaded >= AgvBatch) SendAgv();
        }
    }

    private void SendAgv()
    {
        Event("INFO", "agv", $"AGV 출발 ({_agvLoaded}개 적재)");
        _agvLoaded = 0;
        _agvCycleIndex = 0;   // going
    }

    // ── 가짜 메시지 만들기 ────────────────────────────────

    private LineStatus MakeLine()
    {
        // 실제 서버(v1.4.1)와 같은 모양: 팔레트 인식·집기만 step으로, 벨트 위 공정은 stations로 보냄
        // (가짜는 건전지를 하나씩 처리하므로 공정에 퍽이 하나뿐)
        var onBelt = _step is "inspect" or "measure" or "home" or "sort";
        var station = _step == "home" ? "sort" : _step;
        var step = _mode is "running" or "paused" ? (onBelt ? (_mode == "paused" ? "paused" : "running") : _step) : null;
        return new LineStatus
        {
            RunId = _runId,
            Mode = _mode,
            Step = step,
            BatteryId = _batteryId == 0 ? null : _batteryId,
            OnBelt = onBelt && _batteryId > 0 ? new List<int> { _batteryId } : new List<int>(),
            Stations = onBelt && _batteryId > 0 && station != null
                ? new Dictionary<string, List<int>> { [station] = new List<int> { _batteryId } }
                : new Dictionary<string, List<int>>(),
            Counts = new Dictionary<string, int>(_counts),
            Agv = new AgvInfo { Loaded = _agvLoaded, Batch = AgvBatch, State = AgvCycle[_agvCycleIndex] },
            Message = _message,
            Ts = Json.Now(),
        };
    }

    private Dictionary<string, DeviceInfo> MakeDevices()
    {
        var devices = new Dictionary<string, DeviceInfo>();
        foreach (var name in DeviceNames)
        {
            var info = new DeviceInfo
            {
                Name = name,
                State = _delayLeft.ContainsKey(name) ? "warn" : "ok",   // central_server.py와 같은 값 (ok / warn / offline)
            };
            if (name == "arm1")
            {
                info.Extra["arm"] = _mode == "estop" ? "stopped" : "ok";   // hb/arm1에는 arm만 있음 (규약 v1.4 3-1)
            }
            devices[name] = info;
        }
        return devices;
    }

    private BatteryResult MakeResult()
    {
        var r = new BatteryResult
        {
            RunId = _runId ?? "",
            BatteryId = _batteryId,
            PalletCell = _cell,
            PickedAt = Json.Now(),
            FinishedAt = Json.Now(),
            RuleVersion = "fake",
        };

        var p = _rnd.NextDouble();
        if (p < 0.08)
        {
            r.Vision = new VisionInfo
            {
                Status = "ok", Result = "defect", Conf = 0.91, Polarity = "minus_up",
                Defects = { new VisionDefect { Type = "scratch", Conf = 0.91, Cam = "front" } },
            };
            r.ElectricalStatus = "skipped";
            (r.Grade, r.Dest, r.Reason) = (Grades.NgVisual, "NG_VISUAL_BOX", "외관 불량");
        }
        else if (p < 0.13)
        {
            r.Vision = new VisionInfo { Status = "ok", Result = "normal", Conf = 0.95, Polarity = "plus_up" };
            r.ElectricalStatus = "skipped";
            (r.Grade, r.Dest, r.Reason) = (Grades.Resort, "RESORT_BOX", "+극이 위");
        }
        else
        {
            r.Vision = new VisionInfo { Status = "ok", Result = "normal", Conf = Math.Round(0.85 + _rnd.NextDouble() * 0.14, 2), Polarity = "minus_up" };
            r.ElectricalStatus = "measured";
            r.Polarity = "normal";
            var v = Math.Round(1.25 + _rnd.NextDouble() * 0.40, 3);
            var ri = Math.Round(0.10 + Math.Pow(_rnd.NextDouble(), 2), 3);
            r.VOpen = v;
            r.RInt = ri;
            r.RLoad = 3.31;
            r.VLoad = Math.Round(v * 3.31 / (3.31 + ri), 3);

            // 흉내용 임시 기준 (실제 기준은 라인 컨트롤러에 있음)
            (r.Grade, r.Dest) = v >= 1.50 && ri <= 0.9 ? (Grades.A, "AGV")
                              : v >= 1.30 && ri <= 1.5 ? (Grades.B, "B_BOX")
                              : (Grades.NgVoltage, "NG_VOLTAGE_BOX");
            if (r.Grade == Grades.NgVoltage)
                r.Reason = v < 1.30 && ri > 1.5 ? "전압 낮음, 내부저항 큼" : v < 1.30 ? "전압 낮음" : "내부저항 큼";
        }
        return r;
    }

    private void Event(string level, string device, string message) => RaiseEvent(new LineEvent
    {
        EventId = Guid.NewGuid().ToString("N"),
        RunId = _runId ?? "",
        Ts = Json.Now(),
        Device = device,
        Level = level,
        Message = message,
    });
}
