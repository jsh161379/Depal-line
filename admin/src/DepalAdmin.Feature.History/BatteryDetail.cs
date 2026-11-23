namespace DepalAdmin.Feature.History;

/// <summary>
/// 건전지 1개의 자세한 검사 기록 (이력 조회 오른쪽 상세 패널).
/// 메시지_형식 v1.4.5 server/results → DB battery_cycles · visual_inspections · visual_defects · electrical_tests · grading_rules
/// </summary>
public sealed class BatteryDetail
{
    /// <summary>DB에서 이 건전지를 찾았는가 (못 찾으면 표의 값만 보여 준다)</summary>
    public bool Found { get; set; }

    // ── 건전지 ──
    public string RunId { get; set; } = "";
    public int BatteryId { get; set; }
    public int? PalletSlot { get; set; }
    public string Grade { get; set; } = "";
    public string? Reason { get; set; }
    public string? Dest { get; set; }

    // ── 시각 ──
    public DateTime? PickedAt { get; set; }      // 로봇팔 1 집기 시작 (picked_at → battery_cycles.started_at)
    public DateTime? InspectedAt { get; set; }
    public DateTime? MeasuredAt { get; set; }
    public DateTime? GradedAt { get; set; }
    public DateTime? SortedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    // ── 외관 검사 (visual_inspections) ──
    /// <summary>PASS(정상) / FAIL(불량) / ERROR(검사 실패·YOLO 결과 없음) / null(기록 없음)</summary>
    public string? VisualResult { get; set; }
    public double? VisualConfidence { get; set; }
    /// <summary>minus_up / plus_up / unknown (세 카메라를 합친 극 방향, 위 카메라 우선)</summary>
    public string? VisualPolarity { get; set; }
    public string? ModelVersion { get; set; }
    public List<DefectItem> Defects { get; } = new();
    /// <summary>불량 목록을 못 읽었을 때 안내 (권한 없음 등)</summary>
    public string? DefectNote { get; set; }

    // ── 전기 측정 (battery_cycles.electrical_status + electrical_tests) ──
    /// <summary>PENDING / SKIPPED / FAILED / COMPLETED</summary>
    public string? ElectricalStatus { get; set; }
    /// <summary>normal / reversed / abnormal</summary>
    public string? MeasuredPolarity { get; set; }
    public double? VOpen { get; set; }
    public double? VLoad { get; set; }
    public double? RLoad { get; set; }
    public double? RInt { get; set; }

    // ── 판정 기준 (grading_rules) ──
    public string? RuleVersion { get; set; }
    public RuleGrade? RuleA { get; set; }
    public RuleGrade? RuleB { get; set; }
    /// <summary>"v_open_first" = 무부하 전압 먼저 (메시지_형식 3장 판정 순서 5~8)</summary>
    public string? RuleOrder { get; set; }
    /// <summary>기준값을 못 읽었을 때 안내</summary>
    public string? RuleNote { get; set; }
}

/// <summary>무부하 전압 최소(V), 내부저항 최대(Ω)</summary>
public sealed record RuleGrade(double VOpenMin, double RIntMax);

/// <summary>외관 불량 1건 (visual_defects)</summary>
public sealed record DefectItem(string Type, double? Confidence, string? Camera)
{
    public string TypeText => Type switch
    {
        "scratch" => "기스",
        "dent" => "찌그러짐",
        "sticker" => "스티커",
        "rust" or "corrosion" => "부식",
        "leak" => "누액",
        "broken" => "파손",
        _ => Type,
    } + (Type is "scratch" or "dent" or "sticker" or "rust" or "corrosion" or "leak" or "broken" ? $" ({Type})" : "");

    public string CameraText => Camera switch
    {
        "front" => "앞",
        "back" => "뒤",
        "top" => "위",
        null or "" => "-",
        _ => Camera,
    };

    public string ConfidenceText => Confidence is double c ? $"{c * 100:0.0}%" : "-";
}
