namespace DepalAdmin.Core.Models;

/// <summary>
/// server/line (retained) — 라인 현재 상태. 통신 규약 v1.2 5장.
/// JSON 이름은 snake_case로 자동 변환된다 (RunId ↔ run_id).
/// </summary>
public sealed class LineStatus
{
    public string? RunId { get; set; }

    /// <summary>idle / running / paused / draining / estop / manual</summary>
    public string Mode { get; set; } = "idle";

    /// <summary>running / paused / draining / scan / pick / wait_operator / estop / reset</summary>
    public string? Step { get; set; }

    /// <summary>마지막으로 벨트에 올린 퍽 번호</summary>
    public int? BatteryId { get; set; }

    /// <summary>지금 벨트 위에 있는 퍽 번호 (올린 순서)</summary>
    public List<int> OnBelt { get; set; } = new();

    /// <summary>키: "A", "B", "NG_VISUAL", "NG_VOLTAGE", "RESORT"</summary>
    public Dictionary<string, int> Counts { get; set; } = new();

    public AgvInfo? Agv { get; set; }

    public string? Message { get; set; }

    public string? Ts { get; set; }

    /// <summary>등급별 개수. "NG"를 넣으면 불량 합계(외관 + 전압 + 이전 NG)를 돌려준다 (예전 화면 코드 호환)</summary>
    public int Count(string grade) => grade == Grades.Ng
        ? Counts.Where(kv => Grades.IsNg(kv.Key)).Sum(kv => kv.Value)
        : Counts.TryGetValue(grade, out var n) ? n : 0;
}

public sealed class AgvInfo
{
    public int Loaded { get; set; }
    public int Batch { get; set; }

    /// <summary>home / going / away / returning</summary>
    public string State { get; set; } = "home";
}
