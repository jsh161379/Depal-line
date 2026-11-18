namespace DepalAdmin.Core.Models;

/// <summary>
/// server/devices 안의 장치 1개.
/// "state" 또는 "status" 칸을 State로, 나머지 칸은 전부 Extra로 받는다.
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>server / controller / arm1 / mega / yolo / agv / db_saver / admin-자리이름</summary>
    public string Name { get; set; } = "";

    /// <summary>central_server.py 기준: unknown(확인 전) / ok / warn(지연) / offline(끊김)</summary>
    public string State { get; set; } = "unknown";

    /// <summary>나머지 값 (arm1의 arm, mega의 serial·belt·queue 등)</summary>
    public Dictionary<string, string> Extra { get; set; } = new();
}
