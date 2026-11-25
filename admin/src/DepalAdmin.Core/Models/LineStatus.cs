namespace DepalAdmin.Core.Models;

/// <summary>
/// server/line (retained) — 라인 현재 상태. 통신 규약 v1.4.1 5장.
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

    /// <summary>
    /// 벨트 위 퍽이 지금 있는 공정 → 퍽 번호 (통신 규약 v1.4.1). 퍽이 없는 공정은 키가 없음.
    /// 키: "inspect"(검사 부스), "measure"(측정 자리), "sort"(분류). 예: {"inspect":[19], "measure":[18], "sort":[17]}
    /// 공정 흐름도는 Step(scan·pick)과 이 값을 함께 보고 칸을 켠다.
    /// </summary>
    public Dictionary<string, List<int>> Stations { get; set; } = new();

    /// <summary>그 공정에 퍽이 하나라도 있으면 true</summary>
    public bool HasStation(string station) => Stations.TryGetValue(station, out var n) && n.Count > 0;

    /// <summary>현재 회차 키: "A", "NG_VISUAL", "NG_VOLTAGE", "RESORT"</summary>
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
