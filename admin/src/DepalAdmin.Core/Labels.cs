namespace DepalAdmin.Core;

/// <summary>
/// 코드 값 → 화면용 한글. 모든 페이지가 같은 말을 쓰도록 여기 모아 둔다.
/// 새 값이 생기면 공통 틀 담당(조시현)에게 알려 주세요.
/// </summary>
public static class Labels
{
    public static string Mode(string? mode) => mode switch
    {
        "idle" => "대기",
        "running" => "가동 중",
        "paused" => "일시정지",
        "draining" => "마무리 중",       // 회차 종료: 새 퍽 안 올리고 벨트 위 퍽만 분류
        "estop" => "비상정지",
        "manual" => "수동",
        null => "-",
        _ => mode,
    };

    public static string Step(string? step) => step switch
    {
        // 규약 v1.2 (퍽 여러 개가 동시에 벨트 위) — 퍽별 단계는 없고 라인 전체 상태만 보냄
        "idle" => "-",
        "running" => "운전 중",
        "paused" => "일시정지",
        "draining" => "벨트 비우는 중",
        "estop" => "비상정지",
        "reset" => "해제 중",
        // 이전 규약 (가짜 데이터 모드에서 아직 씀)
        "scan" => "팔레트 인식",
        "pick" => "퍽 올리기",
        "inspect" => "외관 검사",
        "measure" => "전압 측정",
        "home" => "원점 복귀",
        "sort" => "분류",
        "wait_agv" => "AGV 대기",
        "wait_operator" => "작업자 대기",
        null => "-",
        _ => step,
    };

    public static string Grade(string? grade) => grade switch
    {
        "A" => "A",
        "B" => "B",
        "NG_VISUAL" => "외관 불량",
        "NG_VOLTAGE" => "전압 불량",
        "RESORT" => "재분류",
        "NG" => "불량(이전)",            // 11/10 이전 데이터
        null or "" => "-",
        _ => grade,
    };

    public static string Dest(string? dest) => dest switch
    {
        "AGV" => "AGV 적재",
        "B_BOX" => "B 분류함",
        "NG_VISUAL_BOX" => "외관불량함",
        "NG_VOLTAGE_BOX" => "전압불량함",
        "RESORT_BOX" => "재분류함",
        "NG_BOX" => "불량함(이전)",
        null => "배출 안 됨",
        _ => dest,
    };

    public static string AgvState(string? state) => state switch
    {
        "home" => "분류 위치",
        "going" => "이동 중",
        "away" => "목적지",
        "returning" => "복귀 중",
        null => "-",
        _ => state,
    };

    public static string Device(string? name) => name switch
    {
        "server" => "중앙 서버",
        "controller" => "라인 컨트롤러",
        "arm1" => "로봇팔 1",
        "pi" => "로봇팔 1 (옛 이름 pi)",
        "mega" => "Mega (벨트·측정)",
        "yolo" => "YOLO 검사",
        "agv" => "AGV",
        "db_saver" => "DB 저장",
        "ui" => "관리자 화면",
        null => "-",
        string s when s.StartsWith("admin-") => int.TryParse(s[6..], out var no)
            ? $"관리자 화면 {no}"                    // admin-01 → 관리자 화면 1 (규약 v1.4)
            : $"관리자 화면 ({s[6..]})",            // 번호를 안 정한 PC (admin-PC이름)
        string s when s.StartsWith("ui-") => $"관리자 화면 ({s[3..]})",      // v1.3까지의 이름 (ui-PC이름)
        _ => name,
    };

    /// <summary>hb/arm1의 gripper 값</summary>
    public static string Gripper(string? state) => state switch
    {
        "normal" => "집게 정상",
        "weak" => "집게 약함 (전자석 병행)",
        "broken" => "집게 고장 (전자석만)",
        null or "" => "-",
        _ => state,
    };

    /// <summary>측정 극성 (normal / reversed / abnormal)</summary>
    public static string Polarity(string? polarity) => polarity switch
    {
        "normal" => "정상",
        "reversed" => "반대 (+극 위)",
        "abnormal" => "접촉 불량",
        null => "-",
        _ => polarity,
    };

    /// <summary>상단 카메라 극 방향 (minus_up / plus_up / unknown)</summary>
    public static string VisionPolarity(string? polarity) => polarity switch
    {
        "minus_up" => "−극 위 (정상)",
        "plus_up" => "+극 위 (거꾸로)",
        "unknown" => "판단 못 함",
        null => "-",
        _ => polarity,
    };

    public static string ElectricalStatus(string? status) => status switch
    {
        "measured" => "측정함",
        "skipped" => "생략",
        "failed" => "측정 실패",
        null => "-",
        _ => status,
    };

    public static string RoleName(Auth.Role role) => role switch
    {
        Auth.Role.Admin => "관리자",
        Auth.Role.Operator => "작업자",
        _ => "보기 전용",
    };

    public static string Level(string? level) => level switch
    {
        "ERROR" => "고장",
        "WARN" => "주의",
        "INFO" => "정보",
        _ => level ?? "-",
    };

    // central_server.py 상태 값: unknown / ok / warn / offline (나머지는 예전 이름 호환)
    public static string DeviceState(string? state) => state?.ToLowerInvariant() switch
    {
        "ok" or "online" or "normal" => "정상",
        "delayed" or "delay" or "warn" => "지연",
        "lost" or "offline" => "끊김",
        null or "" or "unknown" => "알 수 없음",
        _ => state!,
    };
}
