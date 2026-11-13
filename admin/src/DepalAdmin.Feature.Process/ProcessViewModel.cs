using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DepalAdmin.Core;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;
using DepalAdmin.Core.Pages;

namespace DepalAdmin.Feature.Process;

/// <summary>
/// 담당: 김초아 — 라인 현황: 공정 흐름도, 분류 개수·현재 상태, AGV.
/// 받는 것: server/line (mode · step · stations · counts · agv), server/events 중 장치가 agv인 것(AGV 기록)
///         server/devices 안의 "agv" 칸 (실제 AGV 하트비트: link · agv_state · carrying, agv_relay_v3)
/// 이 화면에 끼워 들어오는 남의 패널:
///   가운데 팔레트 맵 — 김하균 (PanelSlots.LinePallet)
///   오른쪽 조작 버튼 — 유동호 (PanelSlots.LineControls)
/// </summary>
public partial class ProcessViewModel : ObservableObject
{
    private const int AgvLogMax = 30;

    /// <summary>
    /// 공정 순서. Step은 server/line의 step 값 (통신 규약 5장).
    /// TODO(김초아): 실제 라인 배치를 보고 이름·설명·순서를 맞추기
    /// </summary>
    public ObservableCollection<Station> Stations { get; } = new()
    {
        new Station("팔레트", "칸 인식", "scan"),
        new Station("로봇팔", "집기", "pick"),
        new Station("검사 부스", "외관·극 방향", "inspect"),
        new Station("측정", "전압·내부저항", "measure"),
        new Station("컨베이어", "원점 복귀", "home"),
        new Station("분류", "분류함에 넣기", "sort"),
        new Station("AGV", "A 운송 대기", "wait_agv", isLast: true),
    };

    public ObservableCollection<LineEvent> AgvLog { get; } = new();

    /// <summary>아래 환경 센서 칸 (온습도 그래프 · 가스 감시) — 조시현, EnvMonitorViewModel.cs</summary>
    public EnvMonitorViewModel Env { get; }

    public FrameworkElement? PalletPanel { get; }
    public FrameworkElement? ControlPanel { get; }

    [ObservableProperty] private string _mode = "idle";
    [ObservableProperty] private string _modeText = "-";
    [ObservableProperty] private string _stepText = "-";
    [ObservableProperty] private string _batteryText = "-";
    [ObservableProperty] private string _runId = "-";
    [ObservableProperty] private string? _serverMessage;

    [ObservableProperty] private int _countA;
    [ObservableProperty] private int _countB;
    [ObservableProperty] private int _countNg;
    [ObservableProperty] private int _countResort;
    [ObservableProperty] private int _total;
    [ObservableProperty] private string _aRatio = "-";

    [ObservableProperty] private string _agvState = "-";
    [ObservableProperty] private string _agvStateKey = "home";
    [ObservableProperty] private string _agvLoad = "-";
    [ObservableProperty] private double _agvFill;

    // ── 실제 AGV 하트비트 (server/devices → "agv") ──
    [ObservableProperty] private string _agvLink = "수신 대기";
    [ObservableProperty] private string _agvLinkKey = "neutral";
    [ObservableProperty] private string _agvCarrying = "-";

    private string? _lineAgvState;      // server/line의 agv.state (서버가 생각하는 위치)
    private string? _realAgvState;      // AGV가 직접 보낸 agv_state (연결됐을 때만)

    public ProcessViewModel(ILineClient client, IEnumerable<IPanel> panels, EnvMonitorViewModel env)
    {
        Env = env;
        PalletPanel = PanelSlots.Find(panels, PanelSlots.LinePallet);
        ControlPanel = PanelSlots.Find(panels, PanelSlots.LineControls);

        client.LineStatusReceived += (_, line) => Apply(line);
        client.EventReceived += (_, ev) =>
        {
            if (ev.Device != "agv") return;
            AgvLog.Insert(0, ev);
            while (AgvLog.Count > AgvLogMax) AgvLog.RemoveAt(AgvLog.Count - 1);
        };
        if (client.LastLine != null) Apply(client.LastLine);

        client.DevicesReceived += (_, devices) => ApplyDevices(devices);
        if (client.LastDevices.Count > 0) ApplyDevices(client.LastDevices);
    }

    private void Apply(LineStatus s)
    {
        Mode = s.Mode;
        ModeText = Labels.Mode(s.Mode);
        StepText = Labels.Step(s.Step);
        BatteryText = s.BatteryId?.ToString() ?? "-";
        RunId = s.RunId ?? "-";
        ServerMessage = s.Message;

        // 공정 흐름: step(scan·pick)으로 팔레트·로봇팔 칸, stations(v1.4.1)로 검사·측정·분류 칸
        // 벨트 위 퍽이 여러 개면 칸도 여러 개 동시에 켜짐. 일시정지·작업자 대기·마무리(draining) 중에도 표시
        //  - 컨베이어(원점 복귀) 칸은 서버에 따로 없어서, 분류로 가는 퍽(sort)이 있을 때 같이 켬 (가짜 데이터와 같은 기준)
        //  - AGV 칸은 UpdateAgvState에서 AGV가 움직일 때 켬
        var live = s.Mode is "running" or "paused" or "draining";
        foreach (var st in Stations)
        {
            if (st.Step == "wait_agv") continue;
            var key = st.Step == "home" ? "sort" : st.Step;
            st.IsActive = live && (st.Step == s.Step || s.HasStation(key));
        }

        CountA = s.Count("A");
        CountB = s.Count("B");
        CountNg = s.Count("NG");
        CountResort = s.Count("RESORT");
        Total = CountA + CountB + CountNg + CountResort;
        ARatio = Total == 0 ? "-" : $"{CountA * 100.0 / Total:0}%";

        if (s.Agv != null)
        {
            _lineAgvState = s.Agv.State;
            UpdateAgvState();
            AgvLoad = $"{s.Agv.Loaded} / {s.Agv.Batch}";
            AgvFill = s.Agv.Batch == 0 ? 0 : Math.Min(100, s.Agv.Loaded * 100.0 / s.Agv.Batch);
        }
    }

    /// <summary>
    /// 실제 AGV 하트비트. agv_relay_v3가 hb/agv에 실어 보내는 값:
    ///   link: ok / offline (블루투스),  agv_state: home / going / away / returning / stopped / estop,  carrying: true / false
    /// 서버 status: ok / warn / offline / unknown (중계 프로그램이 살아 있는지)
    /// </summary>
    private void ApplyDevices(IReadOnlyDictionary<string, DeviceInfo> devices)
    {
        _realAgvState = null;
        if (!devices.TryGetValue("agv", out var agv) || agv.State is "unknown" or "")
        {
            (AgvLink, AgvLinkKey, AgvCarrying) = ("수신 대기", "neutral", "-");
        }
        else if (agv.State is "offline" or "lost")
        {
            (AgvLink, AgvLinkKey, AgvCarrying) = ("중계 꺼짐", "offline", "-");
        }
        else
        {
            var link = agv.Extra.TryGetValue("link", out var l) ? l : "ok";
            if (link == "offline")
            {
                (AgvLink, AgvLinkKey, AgvCarrying) = ("블루투스 끊김", "offline", "-");
            }
            else
            {
                (AgvLink, AgvLinkKey) = agv.State is "warn" or "delayed" ? ("지연", "warn") : ("정상", "ok");
                if (agv.Extra.TryGetValue("agv_state", out var st) && st is not ("" or "unknown"))
                    _realAgvState = st;
                AgvCarrying = agv.Extra.TryGetValue("carrying", out var c)
                    ? (c == "true" ? "팔레트 실음" : "비어 있음")
                    : "-";
            }
        }
        UpdateAgvState();
    }

    /// <summary>위치: AGV가 직접 보낸 값이 있으면 그것, 없으면 서버(server/line)가 아는 값</summary>
    private void UpdateAgvState()
    {
        var key = _realAgvState ?? _lineAgvState;
        AgvStateKey = key ?? "home";
        // 공정 흐름 AGV 칸: 이동 중·목적지·복귀 중이면 켬
        Stations[^1].IsActive = key is "going" or "away" or "returning";
        AgvState = key switch
        {
            "stopped" => "멈춤 (위치 모름)",
            "estop" => "비상정지",
            _ => Labels.AgvState(key),
        };
    }
}

/// <summary>공정 흐름도의 한 칸</summary>
public partial class Station : ObservableObject
{
    public Station(string title, string sub, string step, bool isLast = false)
    {
        Title = title;
        Sub = sub;
        Step = step;
        IsLast = isLast;
    }

    public string Title { get; }
    public string Sub { get; }
    public string Step { get; }
    public bool IsLast { get; }

    [ObservableProperty] private bool _isActive;
}
