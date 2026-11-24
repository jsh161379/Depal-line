namespace BareunOrder.Feature.Shipping;

/// <summary>국내 택배 송장 형식 검사. 실제 접수·배송 여부를 조회하는 기능은 아니다.</summary>
public static class TrackingRules
{
    // Coupang Developer Center 택배사 규격. 한진·로젠은 현재 공식 조회 규격을 우선한다.
    // https://developers.coupang.com/ko/api/logistics/courier-code
    // https://www.hanjin.com/kor/CMS/DeliveryMgr/WaybillSch.do?mCode=MN038 (12·14자리)
    // https://www.ilogen.com/web/personal/tkSearch (11자리)
    public static IReadOnlyList<string> Couriers { get; } =
        new[] { "CJ대한통운", "한진택배", "롯데택배", "우체국택배", "로젠택배" };

    private static int[] Lengths(string? courier) => courier switch
    {
        "CJ대한통운" => new[] { 10, 12 },
        "한진택배" => new[] { 12, 14 },
        "롯데택배" => new[] { 10, 12, 13 },
        "우체국택배" => new[] { 13 },
        "로젠택배" => new[] { 11 },
        _ => Array.Empty<int>(),
    };

    public static string Normalize(string? value) =>
        new((value ?? "").Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());

    public static string Hint(string? courier) =>
        $"숫자 {string.Join("·", Lengths(courier))}자리 · 공백과 하이픈은 자동 제거";

    public static string? Error(string? courier, string? tracking)
    {
        var lengths = Lengths(courier);
        if (lengths.Length == 0) return "택배사를 선택해 주세요.";
        var number = Normalize(tracking);
        if (number.Length == 0) return "송장번호를 입력해 주세요.";
        if (number.Any(c => c is < '0' or > '9')) return "송장번호는 숫자만 입력할 수 있습니다.";
        if (!lengths.Contains(number.Length))
            return $"{courier} 송장번호는 숫자 {string.Join(" 또는 ", lengths)}자리입니다. (현재 {number.Length}자리)";
        return null;
    }
}
