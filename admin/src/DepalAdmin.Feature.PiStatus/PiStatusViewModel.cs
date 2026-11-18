using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;

namespace DepalAdmin.Feature.PiStatus;

/// <summary>
/// 로봇팔 1 세부: 로봇팔 · 아두이노(Mega) 연결.
/// 받는 것: server/devices 안의
///   "arm1" 칸 (hb/arm1에 실어 보낸 값, 통신 규약 v1.4 3-1)  arm: ok / stopped
///   "mega" 칸의 state (unknown / ok / warn / offline)       → 아두이노 줄
/// v1.4: 장치 이름 pi → arm1. 집게(gripper)는 하트비트에 없어서 뺌 (전자석 대체 구성 없음)
/// TODO(노원종): 설명 문구를 현장에서 할 일로 다듬기
/// </summary>
public partial class PiStatusViewModel : ObservableObject
{
    private const string ArmName = "arm1";
    private const string MegaName = "mega";

    public ObservableCollection<PiItem> Items { get; } = new()
    {
        new PiItem("arm", "로봇팔"),
        new PiItem("arduino", "아두이노 (컨베이어·측정)"),
    };

    [ObservableProperty] private string _piState = "수신 대기";
    [ObservableProperty] private string _piStateKey = "neutral";

    public PiStatusViewModel(ILineClient client)
    {
        client.DevicesReceived += (_, devices) => Apply(devices);
        if (client.LastDevices.Count > 0) Apply(client.LastDevices);
    }

    private void Apply(IReadOnlyDictionary<string, DeviceInfo> devices)
    {
        // 아두이노 줄은 로봇팔 1과 상관없이 Mega 장치 상태로 표시
        devices.TryGetValue(MegaName, out var mega);
        var megaValue = mega?.State.ToLowerInvariant() switch
        {
            null => null,
            "ok" or "online" or "normal" => "ok",
            "delayed" or "delay" or "warn" => "delayed",
            "unknown" or "" => "unknown",
            _ => "offline",
        };

        if (!devices.TryGetValue(ArmName, out var arm))
        {
            PiState = "로봇팔 1 정보 없음";
            PiStateKey = "warn";
            foreach (var item in Items)
                if (item.Key == "arduino") item.Update(megaValue);
            return;
        }

        PiState = Core.Labels.DeviceState(arm.State);
        PiStateKey = arm.State;
        foreach (var item in Items)
            item.Update(item.Key == "arduino"
                ? megaValue
                : arm.Extra.TryGetValue(item.Key, out var v) ? v : null);
    }
}

public partial class PiItem : ObservableObject
{
    public PiItem(string key, string name)
    {
        Key = key;
        Name = name;
    }

    public string Key { get; }
    public string Name { get; }

    [ObservableProperty] private string _value = "-";
    [ObservableProperty] private string _valueText = "-";
    [ObservableProperty] private string _help = "";

    public void Update(string? value)
    {
        Value = value ?? "unknown";
        (ValueText, Help) = (Key, value) switch
        {
            ("arm", "ok") => ("정상", ""),
            ("arm", "stopped") => ("멈춤", "비상정지 중이거나 팔이 멈췄습니다. 비상정지 해제 후 원점 복귀를 확인하세요."),
            ("arduino", "ok") => ("연결됨", ""),
            ("arduino", "unknown") => ("확인 전", "서버가 Mega 신호를 아직 받지 못했습니다."),
            ("arduino", "delayed") => ("지연", "Mega 신호가 늦게 옵니다. 계속되면 Mega 중계 프로그램을 확인하세요."),
            ("arduino", "offline") => ("끊김", "컨베이어·전압 측정이 안 됩니다. 서버 PC와 Mega USB 선, Mega 중계 프로그램을 확인하세요."),
            ("arduino", null) => ("값 없음", "서버가 보낸 장치 목록에 Mega가 없습니다."),
            (_, null) => ("값 없음", "로봇팔 1 하트비트에 이 값이 없습니다."),
            _ => (value!, ""),
        };
    }
}
