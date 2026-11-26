namespace DepalAdmin.Core;

/// <summary>
/// 등급 코드 목록과 판단 규칙 (메시지_형식 v1.4). 화면마다 "NG"를 따로 비교하지 말고 여기 것을 쓴다.
/// 화면 글자는 Labels.Grade, 색은 GradeToBrush.
/// </summary>
public static class Grades
{
    public const string A = "A";
    public const string B = "B";                    // 이전 회차에 저장된 코드
    public const string NgVisual = "NG_VISUAL";     // 외관 불량
    public const string NgVoltage = "NG_VOLTAGE";   // 전압·내부저항 불량
    public const string Resort = "RESORT";          // 재분류 (불량 아님)

    /// <summary>11/10 이전 데이터의 불량. LineStatus.Count(Grades.Ng)는 불량 합계를 돌려준다</summary>
    public const string Ng = "NG";

    /// <summary>화면에 보여 줄 순서 (필터 목록·통계 막대·보고서 칸)</summary>
    public static readonly string[] All = { A, NgVisual, NgVoltage, Resort };

    /// <summary>선택한 기간에 실제 기록이 있는 이전 등급을 집계에 추가한다.</summary>
    public static IReadOnlyList<string> ForReport(IEnumerable<string> recordedGrades)
    {
        var recorded = recordedGrades.ToHashSet();
        return All.Concat(new[] { B, Ng }.Where(recorded.Contains)).ToArray();
    }

    /// <summary>불량인가? (외관 불량, 전압 불량, 이전 NG). 재분류는 불량이 아님</summary>
    public static bool IsNg(string? grade) => grade is NgVisual or NgVoltage or Ng;
}
