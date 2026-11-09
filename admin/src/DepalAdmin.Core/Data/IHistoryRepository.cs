namespace DepalAdmin.Core.Data;

/// <summary>
/// 지난 이력 조회 (MySQL 읽기 전용). 이력 조회(장성찬)·통계·보고서(조시현)가 같이 쓴다.
/// 실제 구현: DepalAdmin.Feature.History/MySqlHistoryRepository.cs
/// 가짜 구현: FakeHistoryRepository (settings.json의 UseFakeDb = true)
/// </summary>
public interface IHistoryRepository
{
    /// <summary>from 이상 to 미만에 끝난 결과. grade가 null이면 전체. 최신이 먼저.</summary>
    Task<IReadOnlyList<HistoryRow>> GetResultsAsync(DateTime from, DateTime to, string? grade, CancellationToken ct = default);

    /// <summary>from 이상 to 미만의 장치·라인 이벤트 개수, 키: INFO / WARN / ERROR (보고서용)</summary>
    Task<IReadOnlyDictionary<string, int>> GetEventCountsAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

/// <summary>battery_cycles 1행 (+ 회차 번호, 전압)</summary>
public sealed class HistoryRow
{
    public string RunId { get; set; } = "";
    public int BatteryId { get; set; }
    public int? PalletSlot { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Grade { get; set; } = "";
    public string? Reason { get; set; }
    public string? Dest { get; set; }
    public double? VOpen { get; set; }
    public double? RInt { get; set; }

    public string GradeText => Labels.Grade(Grade);
    public string DestText => Labels.Dest(Dest);
}
